using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Domain.Communications;
using Base.Domain.Notifications;

namespace Base.Application.Communications;

/// <summary>
/// Messages people write: to people picked one by one, or (Bulk messages) to everyone an audience's filters match or a
/// pasted list, now or at a set time. Delivery goes through <see cref="INotificationService"/>, so preferences, the
/// delivery log, retries and channel settings all apply.
/// </summary>
public interface IMessagingService
{
    Task<IReadOnlyList<AudienceView>> AudiencesAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse<AudienceReach>> ReachAsync(AudienceChoice audience, CancellationToken cancellationToken = default);

    /// <summary>People to pick from, by name or address.</summary>
    Task<ValidationResponse<IReadOnlyList<AudienceMember>>> SearchAsync(string audienceCode, string term, CancellationToken cancellationToken = default);

    /// <summary>The people already picked, to show their names.</summary>
    Task<IReadOnlyList<AudienceMember>> ChosenAsync(string audienceCode, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    /// <summary>What the first recipient would receive.</summary>
    Task<ValidationResponse<MessagePreview>> PreviewAsync(MessageDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Sends the message to the current user only, filled in as for the first recipient. Not counted or logged with sends.</summary>
    Task<ValidationResponse> SendTestAsync(MessageDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Sends it now (in the background, straight after saving) or schedules it.</summary>
    Task<ValidationResponse<Guid>> SendAsync(MessageDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Changes a scheduled message (wording, audience or time) before it goes out.</summary>
    Task<ValidationResponse> UpdateAsync(Guid id, MessageDraft draft, CancellationToken cancellationToken = default);

    Task<ValidationResponse> CancelAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<BroadcastSummary>> ListAsync(BroadcastQuery query, CancellationToken cancellationToken = default);

    Task<ValidationResponse<BroadcastDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<BroadcastDelivery>> DeliveriesAsync(Guid id, NotificationStatus? status, int page, CancellationToken cancellationToken = default);
}

internal sealed class MessagingService(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    ITenantEntitlements entitlements,
    AudienceResolver audiences,
    IRichText richText,
    INotificationService notifications,
    TimeProvider time) : IMessagingService
{
    /// <summary>Picking more than this many one by one is a bulk message.</summary>
    public const int MaxChosen = 50;

    public async Task<IReadOnlyList<AudienceView>> AudiencesAsync(CancellationToken cancellationToken = default)
    {
        var bulk = await CanSendBulkAsync(cancellationToken);
        var views = new List<AudienceView>();
        foreach (var source in audiences.Available())
        {
            views.Add(new AudienceView(source.Code, source.Name, source.Description, source.Icon, AudienceFields.Common.Concat(source.Fields).ToList(), await source.FiltersAsync(cancellationToken), bulk));
        }

        return views;
    }

    public async Task<ValidationResponse<AudienceReach>> ReachAsync(AudienceChoice audience, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audience);
        var allowed = await EnsureAllowedAsync(audience, cancellationToken);
        if (allowed.IsError)
        {
            return allowed.As<AudienceReach>();
        }

        var resolved = await audiences.ResolveAsync(audience.Kind, audience.AudienceCode, audience, cancellationToken);
        if (resolved.IsError)
        {
            return resolved.As<AudienceReach>();
        }

        var (label, fields, members, problems) = resolved.Data!;
        return new AudienceReach(
            label,
            members.Count,
            members.Count(m => !string.IsNullOrWhiteSpace(m.Email) && m.AlreadyReached?.Contains(NotificationChannel.Email) != true),
            members.Count(m => m.Phone is not null && m.AlreadyReached?.Contains(NotificationChannel.Sms) != true),
            members.Count(m => m.UserId is not null && m.AlreadyReached?.Contains(NotificationChannel.InApp) != true),
            members.Take(5).ToList(),
            fields,
            problems);
    }

    public async Task<ValidationResponse<IReadOnlyList<AudienceMember>>> SearchAsync(string audienceCode, string term, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Messages.Send) || !await entitlements.HasFeatureAsync(FeatureCodes.Messaging, cancellationToken))
        {
            return ValidationResponse.Forbidden("You can't send messages.").As<IReadOnlyList<AudienceMember>>();
        }

        if (audiences.Find(audienceCode) is not { } source)
        {
            return ValidationResponse.NotFound("That audience isn't available.").As<IReadOnlyList<AudienceMember>>();
        }

        return string.IsNullOrWhiteSpace(term)
            ? Array.Empty<AudienceMember>()
            : ValidationResponse.Success(await source.SearchAsync(term.Trim(), 10, cancellationToken));
    }

    public async Task<IReadOnlyList<AudienceMember>> ChosenAsync(string audienceCode, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default) =>
        keys.Count == 0 || !currentUser.HasPermission(PermissionCodes.Messages.Send) || audiences.Find(audienceCode) is not { } source
            ? []
            : await source.FindAsync(keys.Take(MaxChosen * 2).ToList(), cancellationToken);

    public async Task<ValidationResponse<MessagePreview>> PreviewAsync(MessageDraft draft, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(draft, forTest: true, cancellationToken);
        if (prepared.IsError)
        {
            return prepared.As<MessagePreview>();
        }

        var (wording, _, members) = prepared.Data!;
        var member = members.Count > 0 ? members[0] : null;
        var values = member is null ? null : wording.ValuesFor(member);
        string? Fill(string? text, bool html) => text is null || values is null
            ? text
            : Common.Text.TemplateRenderer.Render(text, WithBuiltIns(values, member!), html);

        var sms = Fill(wording.SmsText, html: false);
        return new MessagePreview(
            member,
            Fill(wording.Subject, html: false),
            Fill(wording.Html, html: true),
            Fill(wording.Text, html: false),
            sms,
            MessageWording.SmsParts(sms),
            values is null ? [] : wording.WhatsAppParameters.Select((_, i) => values["WhatsApp" + (i + 1)] ?? string.Empty).ToList());
    }

    public async Task<ValidationResponse> SendTestAsync(MessageDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (currentUser.UserId is not { } userId)
        {
            return ValidationResponse.Forbidden("Only a signed-in person can send a test to themselves.");
        }

        var prepared = await PrepareAsync(draft, forTest: true, cancellationToken);
        if (prepared.IsError)
        {
            return prepared;
        }

        var (wording, _, members) = prepared.Data!;
        var sample = members.Count > 0 ? members[0] : new AudienceMember("me", currentUser.AuditName, null, null, userId, new Dictionary<string, string?>(StringComparer.Ordinal));
        var values = wording.ValuesFor(sample);
        var sent = await notifications.SendAsync(
            new Notification(NotificationTemplates.Communications.Message, NotificationRecipient.ToUser(userId), values, draft.Channels, wording.ToContent()),
            cancellationToken);
        return sent.IsSuccess
            ? ValidationResponse.Success($"Test sent to you{(members.Count > 0 ? $", filled in as for {sample.Name}" : string.Empty)}. Channels you have no address for are skipped; see Activity > Messages.")
            : sent;
    }

    public async Task<ValidationResponse<Guid>> SendAsync(MessageDraft draft, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(draft, forTest: false, cancellationToken);
        if (prepared.IsError)
        {
            return prepared.As<Guid>();
        }

        var (wording, label, _) = prepared.Data!;
        var broadcast = new Broadcast(wording.Title, draft.Audience.Kind, draft.Audience.AudienceCode, label, AudienceJson(draft.Audience), currentUser.UserId!.Value, currentUser.AuditName);
        Apply(broadcast, draft, wording);
        await unitOfWork.Repository<Broadcast>().AddAsync(broadcast, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(broadcast.Id, broadcast.IsScheduled ? $"\"{broadcast.Title}\" is scheduled." : $"\"{broadcast.Title}\" is on its way.");
    }

    public async Task<ValidationResponse> UpdateAsync(Guid id, MessageDraft draft, CancellationToken cancellationToken = default)
    {
        var broadcast = await unitOfWork.Repository<Broadcast>().GetAsync(b => b.Id == id, cancellationToken: cancellationToken);
        if (broadcast is null)
        {
            return ValidationResponse.NotFound("Message not found.");
        }

        if (!broadcast.CanChange)
        {
            return ValidationResponse.Conflict("It has already gone out, or was cancelled.");
        }

        var prepared = await PrepareAsync(draft, forTest: false, cancellationToken);
        if (prepared.IsError)
        {
            return prepared;
        }

        var (wording, label, _) = prepared.Data!;
        broadcast.Rename(wording.Title);
        broadcast.SetAudience(draft.Audience.Kind, draft.Audience.AudienceCode, label, AudienceJson(draft.Audience));
        Apply(broadcast, draft, wording);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(broadcast.IsScheduled ? $"\"{broadcast.Title}\" saved." : $"\"{broadcast.Title}\" is on its way.");
    }

    public async Task<ValidationResponse> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Messages.Send))
        {
            return ValidationResponse.Forbidden();
        }

        var broadcast = await unitOfWork.Repository<Broadcast>().GetAsync(b => b.Id == id, cancellationToken: cancellationToken);
        if (broadcast is null)
        {
            return ValidationResponse.NotFound("Message not found.");
        }

        if (!broadcast.CanChange)
        {
            return ValidationResponse.Conflict("It has already gone out, or was cancelled.");
        }

        broadcast.Cancel();
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success($"\"{broadcast.Title}\" won't be sent.");
    }

    public async Task<PagedResult<BroadcastSummary>> ListAsync(BroadcastQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var status = query.Status;
        var page = await unitOfWork.Repository<Broadcast>().GetPagedAsync(
            new PageRequest(query.Page, query.PageSize),
            b => new BroadcastRow(b.Id, b.Title, b.AudienceLabel, b.Channels, b.Status, b.ScheduledFor, b.IsScheduled, b.SenderName, b.RecipientCount, b.QueuedCount, b.SkippedCount),
            q => q.OrderByDescending(b => b.ScheduledFor).ThenByDescending(b => b.Id),
            b => status == null || b.Status == status,
            cancellationToken);
        return new PagedResult<BroadcastSummary>(page.Items.Select(r => r.ToSummary()).ToList(), page.Total, page.Page, page.PageSize);
    }

    public async Task<ValidationResponse<BroadcastDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var broadcast = await unitOfWork.Repository<Broadcast>().GetAsync(b => b.Id == id, asNoTracking: true, cancellationToken: cancellationToken);
        if (broadcast is null)
        {
            return ValidationResponse.NotFound("Message not found.").As<BroadcastDetails>();
        }

        var statuses = await unitOfWork.Repository<NotificationMessage>().GetAllAsync(m => m.Status, m => m.SourceId == id, cancellationToken: cancellationToken);
        var delivery = new DeliveryCounts(
            statuses.Count(s => s is NotificationStatus.Queued or NotificationStatus.Retrying),
            statuses.Count(s => s == NotificationStatus.Sent),
            statuses.Count(s => s == NotificationStatus.Failed),
            statuses.Count(s => s == NotificationStatus.Skipped));
        return new BroadcastDetails(Summary(broadcast), DraftOf(broadcast), broadcast.HtmlBody, broadcast.TextBody, delivery, broadcast.StartedAt, broadcast.CompletedAt, broadcast.Error);
    }

    public Task<PagedResult<BroadcastDelivery>> DeliveriesAsync(Guid id, NotificationStatus? status, int page, CancellationToken cancellationToken = default) =>
        unitOfWork.Repository<NotificationMessage>().GetPagedAsync(
            new PageRequest(page, PageRequest.DefaultPageSize),
            m => new BroadcastDelivery(m.Id, m.RecipientName, m.RecipientAddress, m.Channel, m.Status, m.LastError, m.SentAt),
            q => q.OrderBy(m => m.RecipientName).ThenBy(m => m.Channel),
            m => m.SourceId == id && (status == null || m.Status == status || (status == NotificationStatus.Queued && m.Status == NotificationStatus.Retrying)),
            cancellationToken);

    internal static BroadcastSummary Summary(Broadcast b) =>
        new(b.Id, b.Title, b.AudienceLabel, NotificationComposer.ParseChannels(b.Channels), b.Status, b.ScheduledFor, b.IsScheduled, b.SenderName, b.RecipientCount, b.QueuedCount, b.SkippedCount);

    internal static MessageDraft DraftOf(Broadcast b)
    {
        var audience = MessagingJson.Read<AudienceChoice>(b.AudienceJson) ?? new AudienceChoice(b.AudienceKind, b.AudienceCode);
        return new MessageDraft(
            b.Title,
            NotificationComposer.ParseChannels(b.Channels),
            b.Subject,
            b.HtmlBody,
            b.SmsBody,
            b.WhatsAppTemplateName,
            b.WhatsAppLanguage,
            MessagingJson.List(b.WhatsAppParameters),
            audience with { Kind = b.AudienceKind, AudienceCode = b.AudienceCode },
            b.IsScheduled ? b.ScheduledFor : null);
    }

    private void Apply(Broadcast broadcast, MessageDraft draft, MessageWording wording)
    {
        broadcast.SetContent(
            NotificationComposer.SerializeChannels(draft.Channels.Distinct()),
            wording.Subject,
            wording.Html,
            wording.Text,
            wording.Sms,
            wording.WhatsAppTemplateName,
            wording.WhatsAppLanguage,
            wording.WhatsAppParameters.Count == 0 ? null : MessagingJson.Write(wording.WhatsAppParameters));
        if (draft.SendAt is { } at)
        {
            broadcast.ScheduleFor(at);
        }
        else
        {
            broadcast.SendNow(time.GetUtcNow());
        }
    }

    /// <summary>Checks everything once: permission, plan, audience, wording, time and the monthly limit.</summary>
    private async Task<ValidationResponse<(MessageWording Wording, string Label, IReadOnlyList<AudienceMember> Members)>> PrepareAsync(MessageDraft draft, bool forTest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var allowed = await EnsureAllowedAsync(draft.Audience, cancellationToken);
        if (allowed.IsError)
        {
            return allowed.As<(MessageWording, string, IReadOnlyList<AudienceMember>)>();
        }

        var resolved = await audiences.ResolveAsync(draft.Audience.Kind, draft.Audience.AudienceCode, draft.Audience, cancellationToken);
        if (resolved.IsError)
        {
            return resolved.As<(MessageWording, string, IReadOnlyList<AudienceMember>)>();
        }

        var (label, fields, members, _) = resolved.Data!;
        var wording = MessageWording.From(draft, richText);
        var errors = wording.Problems(draft.Channels, fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal))
            .Select(p => new ValidationError(p.Field, p.Message)).ToList();
        if (!forTest)
        {
            if (members.Count == 0)
            {
                errors.Add(new ValidationError(nameof(MessageDraft.Audience), "Nobody matches; choose someone to send it to."));
            }

            var now = time.GetUtcNow();
            if (draft.SendAt is { } at && (at < now.AddMinutes(1) || at > now.AddYears(1)))
            {
                errors.Add(new ValidationError(nameof(MessageDraft.SendAt), "Choose a time from a minute from now to a year ahead."));
            }
        }

        if (errors.Count > 0)
        {
            return ValidationResponse.Failure(errors).As<(MessageWording, string, IReadOnlyList<AudienceMember>)>();
        }

        if (!forTest)
        {
            var limit = await entitlements.EnsureWithinLimitAsync(LimitCodes.MessagesPerMonth, (long)members.Count * draft.Channels.Distinct().Count(), cancellationToken);
            if (limit.IsError)
            {
                return limit.As<(MessageWording, string, IReadOnlyList<AudienceMember>)>();
            }
        }

        return (wording, label, members);
    }

    private async Task<ValidationResponse> EnsureAllowedAsync(AudienceChoice audience, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audience);
        if (!currentUser.HasPermission(PermissionCodes.Messages.Send))
        {
            return ValidationResponse.Forbidden("You can't send messages.");
        }

        if (!await entitlements.HasFeatureAsync(FeatureCodes.Messaging, cancellationToken))
        {
            return ValidationResponse.Forbidden("Messaging isn't included in your organisation's plan.");
        }

        var bulk = audience.Kind != BroadcastAudienceKind.Chosen || (audience.Keys?.Count ?? 0) > MaxChosen;
        if (bulk && !await CanSendBulkAsync(cancellationToken))
        {
            return ValidationResponse.Forbidden(await entitlements.HasFeatureAsync(FeatureCodes.BulkMessaging, cancellationToken)
                ? "Sending to a whole audience or a list needs the Send bulk messages permission."
                : $"Bulk messages aren't included in your organisation's plan; pick up to {MaxChosen} people instead.");
        }

        return ValidationResponse.Success();
    }

    private async Task<bool> CanSendBulkAsync(CancellationToken cancellationToken) =>
        currentUser.HasPermission(PermissionCodes.Messages.SendBulk) && await entitlements.HasFeatureAsync(FeatureCodes.BulkMessaging, cancellationToken);

    private static string? AudienceJson(AudienceChoice audience) => audience.Kind switch
    {
        BroadcastAudienceKind.Chosen => MessagingJson.Write(new AudienceChoice(audience.Kind, audience.AudienceCode, Keys: audience.Keys?.Distinct(StringComparer.Ordinal).ToList() ?? [])),
        BroadcastAudienceKind.Filtered => MessagingJson.Write(new AudienceChoice(audience.Kind, audience.AudienceCode, Filters: Clean(audience.Filters))),
        _ => MessagingJson.Write(new AudienceChoice(audience.Kind, audience.AudienceCode, ListText: audience.ListText)),
    };

    internal static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string>? filters) =>
        (filters ?? new Dictionary<string, string>(StringComparer.Ordinal)).Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToDictionary(f => f.Key, f => f.Value.Trim(), StringComparer.Ordinal);

    private static Dictionary<string, string?> WithBuiltIns(Dictionary<string, string?> values, AudienceMember member)
    {
        var all = new Dictionary<string, string?>(values, StringComparer.Ordinal);
        all.TryAdd(NotificationCatalog.RecipientNameVariable, member.Name);
        all.TryAdd(NotificationCatalog.AppNameVariable, "this application");
        all.TryAdd(NotificationCatalog.PublicBaseUrlVariable, string.Empty);
        return all;
    }
}

internal sealed record BroadcastRow(
    Guid Id, string Title, string AudienceLabel, string Channels, BroadcastStatus Status, DateTimeOffset ScheduledFor, bool IsScheduled, string SenderName, int RecipientCount, int QueuedCount, int SkippedCount)
{
    public BroadcastSummary ToSummary() =>
        new(Id, Title, AudienceLabel, NotificationComposer.ParseChannels(Channels), Status, ScheduledFor, IsScheduled, SenderName, RecipientCount, QueuedCount, SkippedCount);
}

/// <summary>Turns an audience choice into its members, removing duplicates, as the current user.</summary>
internal sealed class AudienceResolver(IEnumerable<IAudienceSource> sources, ICurrentUser currentUser, Settings.IApplicationSettingService settings)
{
    public IEnumerable<IAudienceSource> Available() => sources.Where(s => currentUser.HasPermission(s.Permission));

    public IAudienceSource? Find(string code) => Available().FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.Ordinal));

    public async Task<ValidationResponse<(string Label, IReadOnlyList<AudienceField> Fields, IReadOnlyList<AudienceMember> Members, IReadOnlyList<string> Problems)>> ResolveAsync(
        BroadcastAudienceKind kind, string code, AudienceChoice choice, CancellationToken cancellationToken)
    {
        var countryCode = await settings.GetAsync(Settings.SettingCodes.Notifications.DefaultCountryCode, cancellationToken) ?? "27";
        if (kind == BroadcastAudienceKind.List)
        {
            var parsed = RecipientList.Parse(choice.ListText);
            if (parsed.Members.Count == 0 && parsed.Problems.Count > 0)
            {
                return ValidationResponse.Failure(new ValidationError(nameof(AudienceChoice.ListText), parsed.Problems[0]))
                    .As<(string, IReadOnlyList<AudienceField>, IReadOnlyList<AudienceMember>, IReadOnlyList<string>)>();
            }

            var members = Distinct(parsed.Members, countryCode);
            return ($"A list of {members.Count:N0}", AudienceFields.Common.Concat(parsed.Fields).ToList(), members, parsed.Problems);
        }

        if (Find(code) is not { } source)
        {
            return ValidationResponse.NotFound("That audience isn't available to you.")
                .As<(string, IReadOnlyList<AudienceField>, IReadOnlyList<AudienceMember>, IReadOnlyList<string>)>();
        }

        var fields = AudienceFields.Common.Concat(source.Fields).ToList();
        if (kind == BroadcastAudienceKind.Chosen)
        {
            var keys = choice.Keys?.Distinct(StringComparer.Ordinal).ToList() ?? [];
            var chosen = Distinct(await source.FindAsync(keys, cancellationToken), countryCode);
            var label = chosen.Count switch
            {
                0 => source.Name,
                1 => chosen[0].Name,
                2 => $"{chosen[0].Name} and {chosen[1].Name}",
                _ => $"{chosen[0].Name} and {chosen.Count - 1} others",
            };
            return (label, fields, chosen, []);
        }

        var filters = MessagingService.Clean(choice.Filters);
        var all = new List<AudienceMember>();
        await foreach (var member in source.MembersAsync(filters, cancellationToken))
        {
            all.Add(member);
        }

        var definitions = await source.FiltersAsync(cancellationToken);
        var described = filters.Select(f => definitions.FirstOrDefault(d => string.Equals(d.Code, f.Key, StringComparison.Ordinal)) is { } d
                ? $"{d.Label}: {d.Options.FirstOrDefault(o => string.Equals(o.Value, f.Value, StringComparison.Ordinal))?.Label ?? f.Value}"
                : null)
            .Where(d => d is not null);
        return (string.Join(" · ", new[] { "Everyone in " + source.Name }.Concat(described!)), fields, Distinct(all, countryCode), []);
    }

    /// <summary>
    /// Nobody gets the same message twice on a channel: an account, email address or phone number (however it is
    /// written) already in the audience isn't used again, and someone with nothing new is left out. Phone numbers are
    /// kept only when they can be dialled, in international form.
    /// </summary>
    private static List<AudienceMember> Distinct(IEnumerable<AudienceMember> members, string countryCode)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AudienceMember>();
        foreach (var raw in members)
        {
            var member = raw with { Phone = PhoneNumbers.ToE164(raw.Phone, countryCode) };
            var account = member.UserId is { } id ? "u:" + id : null;
            var email = string.IsNullOrWhiteSpace(member.Email) ? null : "e:" + member.Email.Trim();
            var phone = member.Phone is null ? null : "p:" + member.Phone;
            var keys = new[] { account, email, phone }.Where(k => k is not null).Select(k => k!).ToList();
            if (keys.Count == 0 || keys.TrueForAll(seen.Contains))
            {
                continue;
            }

            var reached = new List<NotificationChannel>();
            if (account is not null && seen.Contains(account))
            {
                reached.Add(NotificationChannel.InApp);
            }

            if (email is not null && seen.Contains(email))
            {
                reached.Add(NotificationChannel.Email);
            }

            if (phone is not null && seen.Contains(phone))
            {
                reached.AddRange([NotificationChannel.Sms, NotificationChannel.WhatsApp]);
            }

            keys.ForEach(k => seen.Add(k));
            result.Add(reached.Count == 0 ? member : member with { AlreadyReached = reached });
        }

        return result;
    }
}
