using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Flags;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Operations;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Domain.Communications;
using Base.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Base.Application.Communications;

/// <summary>
/// Sends a broadcast as the person who wrote it, so their permissions and plan decide who it reaches. Each recipient
/// becomes one notification per channel, linked back to the broadcast; an interrupted send carries on where it stopped.
/// </summary>
internal sealed partial class BroadcastSender(
    IUnitOfWork unitOfWork,
    IRunAsUser runAs,
    ICurrentUser currentUser,
    ITenantEntitlements entitlements,
    IFeatureFlags flags,
    AudienceResolver audiences,
    INotificationService notifications,
    TimeProvider time,
    ILogger<BroadcastSender> logger)
{
    public async Task<string> SendAsync(Guid broadcastId, CancellationToken cancellationToken)
    {
        if (!await flags.IsOnAsync(FeatureFlagCodes.MessageSending, cancellationToken))
        {
            // Paused by the kill switch: it stays scheduled and the scheduled-messages job sends it once it's back on.
            return "Message sending is paused";
        }

        var broadcast = await unitOfWork.Repository<Broadcast>().GetAsync(b => b.Id == broadcastId, cancellationToken: cancellationToken);
        if (broadcast is null || !broadcast.TryStart(time.GetUtcNow()))
        {
            return "Nothing to send";
        }

        await unitOfWork.SaveAsync(cancellationToken);
        using var actingAs = await runAs.BeginAsync(broadcast.SenderUserId, cancellationToken);
        if (actingAs is null)
        {
            return await FailAsync(broadcast, $"{broadcast.SenderName} can no longer sign in, so it wasn't sent.", 0, cancellationToken);
        }

        var draft = MessagingService.DraftOf(broadcast);
        var bulk = broadcast.AudienceKind != BroadcastAudienceKind.Chosen || (draft.Audience.Keys?.Count ?? 0) > MessagingService.MaxChosen;
        if (!currentUser.HasPermission(PermissionCodes.Messages.Send) || !await entitlements.HasFeatureAsync(FeatureCodes.Messaging, cancellationToken)
            || (bulk && (!currentUser.HasPermission(PermissionCodes.Messages.SendBulk) || !await entitlements.HasFeatureAsync(FeatureCodes.BulkMessaging, cancellationToken))))
        {
            return await FailAsync(broadcast, $"{broadcast.SenderName} or the plan no longer allows this message, so it wasn't sent.", 0, cancellationToken);
        }

        var resolved = await audiences.ResolveAsync(broadcast.AudienceKind, broadcast.AudienceCode, draft.Audience, cancellationToken);
        if (resolved.IsError)
        {
            return await FailAsync(broadcast, resolved.Message ?? "The audience couldn't be read.", 0, cancellationToken);
        }

        var members = resolved.Data!.Members;
        var channels = NotificationComposer.ParseChannels(broadcast.Channels);
        if (broadcast.Processed == 0)
        {
            var limit = await entitlements.EnsureWithinLimitAsync(LimitCodes.MessagesPerMonth, (long)members.Count * channels.Count, cancellationToken);
            if (limit.IsError)
            {
                return await FailAsync(broadcast, limit.Message ?? "The plan's monthly message limit was reached.", members.Count, cancellationToken);
            }
        }

        var wording = MessageWording.From(broadcast);
        var content = wording.ToContent();
        foreach (var member in members.Skip(broadcast.Processed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            broadcast.Advance();
            var recipient = new NotificationRecipient(member.UserId, member.Email, member.Phone, member.Name);
            var toSend = member.AlreadyReached is { Count: > 0 } reached ? channels.Except(reached).ToList() : channels;
            if (toSend.Count == 0)
            {
                continue;
            }

            var sent = await notifications.SendAsync(
                new Notification(NotificationTemplates.Communications.Message, recipient, wording.ValuesFor(member), toSend, content, broadcast.Id),
                cancellationToken);
            if (sent.IsError)
            {
                // The account went away since the audience was read; the progress above still needs saving.
                await unitOfWork.SaveAsync(cancellationToken);
            }
        }

        var statuses = await unitOfWork.Repository<NotificationMessage>().GetAllAsync(m => m.Status, m => m.SourceId == broadcast.Id, cancellationToken: cancellationToken);
        var skipped = statuses.Count(s => s == NotificationStatus.Skipped);
        broadcast.Complete(members.Count, statuses.Count - skipped, skipped, time.GetUtcNow());
        await unitOfWork.SaveAsync(cancellationToken);
        LogSent(logger, broadcast.Id, members.Count, statuses.Count - skipped, skipped);
        return $"\"{broadcast.Title}\": {members.Count:N0} recipient(s), {statuses.Count - skipped:N0} message(s) queued, {skipped:N0} skipped";
    }

    private async Task<string> FailAsync(Broadcast broadcast, string error, int recipients, CancellationToken cancellationToken)
    {
        broadcast.Fail(error, recipients, 0, 0, time.GetUtcNow());
        await unitOfWork.SaveAsync(cancellationToken);
        LogFailed(logger, broadcast.Id, error);
        return error;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Broadcast {BroadcastId} sent to {Recipients} recipient(s): {Queued} queued, {Skipped} skipped")]
    private static partial void LogSent(ILogger logger, Guid broadcastId, int recipients, int queued, int skipped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broadcast {BroadcastId} was not sent: {Error}")]
    private static partial void LogFailed(ILogger logger, Guid broadcastId, string error);
}

/// <summary>A message sent "now": delivered by the outbox straight after it was saved, in its tenant.</summary>
internal sealed class BroadcastDueHandler(BroadcastSender sender) : IDomainEventHandler<BroadcastDue>
{
    public async Task HandleAsync(BroadcastDue domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await sender.SendAsync(domainEvent.BroadcastId, cancellationToken);
    }
}

/// <summary>
/// Sends scheduled messages when their time comes, in each one's own tenant. Also the safety net for a "send now"
/// the outbox didn't get to, and for a send that stopped part-way.
/// </summary>
internal sealed class ScheduledMessagesJob(IUnitOfWork unitOfWork, ITenantContext tenant, IFeatureFlags flags, IServiceScopeFactory scopes, TimeProvider time) : IScheduledJob
{
    public string Code => "scheduled-messages";

    public string Name => "Scheduled messages";

    public string Description => "Sends messages scheduled for a set time, and finishes any send that stopped part-way.";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var stale = now.AddMinutes(-10);
        List<(Guid Id, Guid TenantId)> due;
        using (tenant.BeginSystemScope())
        {
            due = (await unitOfWork.Repository<Broadcast>().GetAllAsync(
                    b => new { b.Id, b.TenantId },
                    b => (b.Status == BroadcastStatus.Scheduled && b.ScheduledFor <= now && (b.IsScheduled || b.ScheduledFor <= stale))
                        || (b.Status == BroadcastStatus.Sending && b.StartedAt <= stale),
                    q => q.OrderBy(b => b.ScheduledFor),
                    take: 200,
                    cancellationToken: cancellationToken))
                .Select(b => (b.Id, b.TenantId)).ToList();
        }

        // Organisations whose sending is paused keep theirs until the switch is back on, without holding up the rest.
        var paused = new HashSet<Guid>();
        foreach (var tenantId in due.Select(d => d.TenantId).Distinct())
        {
            if (!await flags.IsOnForAsync(FeatureFlagCodes.MessageSending, tenantId, cancellationToken))
            {
                paused.Add(tenantId);
            }
        }

        var waiting = due.Count(d => paused.Contains(d.TenantId));
        due = due.Where(d => !paused.Contains(d.TenantId)).Take(20).ToList();

        foreach (var (id, tenantId) in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, null);
            await scope.ServiceProvider.GetRequiredService<BroadcastSender>().SendAsync(id, cancellationToken);
        }

        var result = due.Count == 0 ? "Nothing due" : $"Sent {due.Count} message(s)";
        return waiting == 0 ? result : $"{result}; {waiting} paused by the message-sending switch";
    }
}
