using System.Text.Json;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace Base.Application.Notifications;

/// <summary>The one way business code notifies people, on any channel.</summary>
public interface INotificationService
{
    /// <summary>
    /// Records the notification and saves the unit of work (so it commits together with the caller's staged changes).
    /// Ordinary messages are delivered afterwards by the outbox, with retries and fallback. Sensitive ones (sign-in
    /// links) are sent immediately and stored without content; if no channel accepts them it throws
    /// <see cref="NotificationDeliveryException"/>, so a caller running in the outbox is retried.
    /// </summary>
    /// <returns>The ids of the messages recorded (queued, sent or skipped), one or more per channel.</returns>
    Task<ValidationResponse<IReadOnlyList<Guid>>> SendAsync(Notification notification, CancellationToken cancellationToken = default);
}

internal sealed partial class NotificationService(
    IUnitOfWork unitOfWork,
    NotificationComposer composer,
    INotificationChannels channels,
    INotificationRecipientResolver recipients,
    TimeProvider time,
    ILogger<NotificationService> logger) : INotificationService
{
    public async Task<ValidationResponse<IReadOnlyList<Guid>>> SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var template = NotificationCatalog.Get(notification.Template);
        if (notification.Content?.WhatsApp is { } whatsApp)
        {
            template = template with { WhatsApp = whatsApp };
        }

        var recipient = await recipients.ResolveAsync(notification.Recipient, cancellationToken);
        if (recipient is null)
        {
            return ValidationResponse.NotFound("The recipient was not found.").As<IReadOnlyList<Guid>>();
        }

        var parts = notification.Content?.Parts ?? await composer.PartsAsync(template.Code, cancellationToken);
        var values = await composer.ValuesAsync(template, notification.Data, recipient, cancellationToken);
        var primary = (notification.Channels ?? template.Channels).Distinct().ToList();
        var fallback = (template.Fallback ?? []).Where(c => !primary.Contains(c)).ToList();

        var messages = new List<NotificationMessage>();
        foreach (var channel in primary)
        {
            var chain = new List<NotificationChannel> { channel };
            chain.AddRange(fallback);
            if (template.IsSensitive)
            {
                await SendSensitiveAsync(template, parts, values, recipient, chain, messages, cancellationToken);
            }
            else
            {
                await QueueFirstAvailableAsync(composer, template, parts, values, recipient, chain, messages, null, cancellationToken);
            }
        }

        if (notification.SourceId is not null || notification.Content?.WhatsApp is not null)
        {
            var whatsAppJson = notification.Content?.WhatsApp is { } approved ? JsonSerializer.Serialize(approved, NotificationComposer.Json) : null;
            foreach (var message in messages)
            {
                message.AttachTo(notification.SourceId, message.Channel == NotificationChannel.WhatsApp ? whatsAppJson : null);
            }
        }

        await unitOfWork.Repository<NotificationMessage>().AddRangeAsync(messages, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);

        if (template.IsSensitive && messages.Any(m => m.Status == NotificationStatus.Failed) && !messages.Any(m => m.Status == NotificationStatus.Sent))
        {
            throw new NotificationDeliveryException($"{template.Code} could not be delivered on any channel.");
        }

        return messages.Select(m => m.Id).ToList();
    }

    /// <summary>Walks the chain and queues the first usable channel; the rest of the chain stays on it as fallback.</summary>
    internal static async Task QueueFirstAvailableAsync(
        NotificationComposer composer,
        NotificationTemplateDefinition template,
        IReadOnlyDictionary<string, string> parts,
        IReadOnlyDictionary<string, string?> values,
        NotificationRecipientSnapshot recipient,
        List<NotificationChannel> chain,
        List<NotificationMessage> messages,
        Guid? fallbackOf,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var channel = chain[i];
            var reason = await composer.UnavailableReasonAsync(template, parts, channel, recipient, cancellationToken);
            if (reason is not null)
            {
                messages.Add(NotificationMessage.RecordSkipped(template.Code, template.Category, channel, recipient, reason));
                continue;
            }

            var replay = new NotificationReplay(
                JsonSerializer.Serialize(values, NotificationComposer.Json),
                JsonSerializer.Serialize(recipient, NotificationComposer.Json),
                NotificationComposer.SerializeChannels(chain.Skip(i + 1)));
            messages.Add(NotificationMessage.Queue(
                template.Code, template.Category, channel, recipient, recipient.AddressFor(channel)!,
                NotificationComposer.Render(template, parts, channel, values), replay, fallbackOf));
            return;
        }
    }

    private async Task SendSensitiveAsync(
        NotificationTemplateDefinition template,
        IReadOnlyDictionary<string, string> parts,
        IReadOnlyDictionary<string, string?> values,
        NotificationRecipientSnapshot recipient,
        List<NotificationChannel> chain,
        List<NotificationMessage> messages,
        CancellationToken cancellationToken)
    {
        Guid? previous = null;
        foreach (var channel in chain)
        {
            var reason = await composer.UnavailableReasonAsync(template, parts, channel, recipient, cancellationToken);
            var provider = reason is null ? await channels.ResolveAsync(channel, cancellationToken) : null;
            if (provider is null)
            {
                messages.Add(NotificationMessage.RecordSkipped(template.Code, template.Category, channel, recipient, reason ?? $"No provider is configured for {channel}."));
                continue;
            }

            var message = NotificationMessage.RecordSensitive(template.Code, template.Category, channel, recipient, recipient.AddressFor(channel)!, previous);
            messages.Add(message);
            var content = NotificationComposer.Render(template, parts, channel, values);
            ProviderResult result;
            try
            {
                result = await provider.SendAsync(NotificationComposer.ToOutgoing(message.Id, channel, recipient, content, template), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = ProviderResult.Failed(exception.Message, isRetryable: true);
            }

            if (result.Succeeded)
            {
                message.MarkSent(provider.Name, result.ProviderMessageId, time.GetUtcNow());
                return;
            }

            message.MarkFailed(provider.Name, result.Error ?? "Unknown error");
            LogSensitiveFailed(logger, template.Code, channel, result.Error);
            previous = message.Id;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sensitive notification {Template} failed on {Channel}: {Error}")]
    private static partial void LogSensitiveFailed(ILogger logger, string template, NotificationChannel channel, string? error);
}
