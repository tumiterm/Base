using System.Text.Json;
using Base.Application.Common.Persistence;
using Base.Application.Messaging;
using Base.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace Base.Application.Notifications;

/// <summary>
/// Delivers a queued message after its transaction committed (outbox handler). Retryable failures are retried up to
/// <see cref="MaxAttempts"/> times through the outbox's backoff; then, or on a permanent failure, the next fallback
/// channel is queued. Idempotent: a message that already reached a final state is left alone.
/// </summary>
internal sealed partial class NotificationDispatcher(
    IUnitOfWork unitOfWork,
    NotificationComposer composer,
    INotificationChannels channels,
    TimeProvider time,
    ILogger<NotificationDispatcher> logger) : IDomainEventHandler<NotificationQueued>
{
    public const int MaxAttempts = 3;
    public const string InboxProvider = "inbox";

    public async Task HandleAsync(NotificationQueued domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var repository = unitOfWork.Repository<NotificationMessage>();
        var message = await repository.GetAsync(m => m.Id == domainEvent.MessageId, cancellationToken: cancellationToken);
        if (message is null || message.IsFinal)
        {
            return;
        }

        if (message.Channel == NotificationChannel.InApp)
        {
            message.MarkSent(InboxProvider, null, time.GetUtcNow());
            await unitOfWork.SaveAsync(cancellationToken);
            return;
        }

        var template = NotificationCatalog.Get(message.TemplateCode);
        if (message.WhatsAppTemplate is { } whatsApp)
        {
            template = template with { WhatsApp = JsonSerializer.Deserialize<WhatsAppTemplate>(whatsApp, NotificationComposer.Json) };
        }

        var provider = await channels.ResolveAsync(message.Channel, cancellationToken);
        if (provider is null)
        {
            message.MarkSkipped($"No provider is available for {message.Channel}.");
            await QueueFallbackAsync(message, template, cancellationToken);
            await unitOfWork.SaveAsync(cancellationToken);
            return;
        }

        ProviderResult result;
        try
        {
            result = await provider.SendAsync(NotificationComposer.ToOutgoing(message, template), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = ProviderResult.Failed(exception.Message, isRetryable: true);
        }

        if (result.Succeeded)
        {
            message.MarkSent(provider.Name, result.ProviderMessageId, time.GetUtcNow());
            await unitOfWork.SaveAsync(cancellationToken);
            return;
        }

        var error = result.Error ?? "Unknown error";
        if (result.IsRetryable && message.Attempts + 1 < MaxAttempts)
        {
            message.MarkRetrying(provider.Name, error);
            await unitOfWork.SaveAsync(cancellationToken);
            throw new NotificationRetryException($"{message.Channel} delivery of {message.Id} failed ({error}); retrying.");
        }

        message.MarkFailed(provider.Name, error);
        LogFailed(logger, message.Id, message.Channel, error);
        await QueueFallbackAsync(message, template, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
    }

    private async Task QueueFallbackAsync(NotificationMessage failed, NotificationTemplateDefinition template, CancellationToken cancellationToken)
    {
        var chain = NotificationComposer.ParseChannels(failed.FallbackChannels);
        if (chain.Count == 0 || failed.DataJson is null || failed.RecipientJson is null)
        {
            return;
        }

        var recipient = JsonSerializer.Deserialize<NotificationRecipientSnapshot>(failed.RecipientJson, NotificationComposer.Json)!;
        var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(failed.DataJson, NotificationComposer.Json)!;
        var parts = await composer.PartsAsync(template.Code, cancellationToken);

        var messages = new List<NotificationMessage>();
        await NotificationService.QueueFirstAvailableAsync(composer, template, parts, values, recipient, chain, messages, failed.Id, cancellationToken);
        await unitOfWork.Repository<NotificationMessage>().AddRangeAsync(messages, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {MessageId} failed on {Channel}: {Error}")]
    private static partial void LogFailed(ILogger logger, Guid messageId, NotificationChannel channel, string error);
}
