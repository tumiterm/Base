using Base.Domain.Notifications;

namespace Base.Application.Notifications;

/// <summary>
/// A request to notify someone. <paramref name="Data"/> is an object or dictionary whose members fill the template's
/// variables. <paramref name="Channels"/> overrides the template's default channels.
/// </summary>
/// <param name="Content">Wording written by a person, used instead of the template's file (the template still sets the
/// category, so preferences apply). Placeholders in it are filled from <paramref name="Data"/>.</param>
/// <param name="SourceId">What this notification belongs to (a broadcast), recorded on each message.</param>
public sealed record Notification(
    string Template,
    NotificationRecipient Recipient,
    object? Data = null,
    IReadOnlyList<NotificationChannel>? Channels = null,
    NotificationContent? Content = null,
    Guid? SourceId = null);

/// <summary>Ad-hoc wording: template parts by <see cref="TemplateParts"/> name, and the approved WhatsApp template.</summary>
public sealed record NotificationContent(IReadOnlyDictionary<string, string> Parts, WhatsAppTemplate? WhatsApp = null);

/// <summary>Who to notify: an account (addresses come from the user record) and/or explicit addresses.</summary>
public sealed record NotificationRecipient(Guid? UserId, string? Email, string? Phone, string? Name)
{
    public static NotificationRecipient ToUser(Guid userId) => new(userId, null, null, null);

    public static NotificationRecipient ToEmail(string email, string? name = null) => new(null, email, null, name);

    public static NotificationRecipient ToPhone(string phone, string? name = null) => new(null, null, phone, name);

    public static NotificationRecipient ToContact(string? email, string? phone, string? name) => new(null, email, phone, name);
}

/// <summary>What a provider is asked to send.</summary>
/// <param name="To">Email address, or E.164 phone number (e.g. +27821234567).</param>
/// <param name="TemplateParameters">WhatsApp: values for the approved template's {{1}}, {{2}}, ...</param>
public sealed record OutgoingMessage(
    Guid MessageId,
    NotificationChannel Channel,
    string To,
    string? ToName,
    string? Subject,
    string? Body,
    string? HtmlBody,
    WhatsAppTemplate? WhatsApp,
    IReadOnlyList<string> TemplateParameters);

/// <summary>A provider's answer. Retryable failures (timeouts, 429, 5xx) are tried again; others fall back at once.</summary>
public sealed record ProviderResult(bool Succeeded, string? ProviderMessageId, string? Error, bool IsRetryable)
{
    public static ProviderResult Sent(string? providerMessageId = null) => new(true, providerMessageId, null, false);

    public static ProviderResult Failed(string error, bool isRetryable) => new(false, null, error, isRetryable);
}

/// <summary>A delivery integration (SMTP, SendGrid, Twilio, Clickatell, BulkSMS, WhatsApp Cloud API, ...).</summary>
public interface INotificationProvider
{
    /// <summary>The configured instance name (e.g. <c>twilio-main</c>), recorded on each message.</summary>
    string Name { get; }

    NotificationChannel Channel { get; }

    Task<ProviderResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>Which channels are switched on, and which provider instance serves each one for the current tenant.</summary>
public interface INotificationChannels
{
    bool IsEnabled(NotificationChannel channel);

    /// <summary>The provider for the channel, or null if the channel is off or has no usable provider. Not used for in-app.</summary>
    Task<INotificationProvider?> ResolveAsync(NotificationChannel channel, CancellationToken cancellationToken = default);

    /// <summary>Where the channel's messages go right now, for the settings screen.</summary>
    Task<ChannelRoute> DescribeAsync(NotificationChannel channel, CancellationToken cancellationToken = default);

    /// <summary>Provider instance names a tenant may choose for the channel.</summary>
    IReadOnlyList<string> ProviderNames(NotificationChannel channel);
}

/// <summary>How a channel delivers. <see cref="Delivers"/> is false when messages only go to the log (or nowhere).</summary>
/// <param name="Source">What chose the provider: the tenant's setting, the Email settings, or configuration.</param>
public sealed record ChannelRoute(bool Enabled, string? ProviderName, bool Delivers, string Description, ChannelRouteSource Source);

public enum ChannelRouteSource
{
    Off = 0,
    Configuration = 1,
    EmailSettings = 2,
    TenantChoice = 3,
}

/// <summary>Fills a recipient's missing addresses and name from their account. Null if the user doesn't exist in this tenant.</summary>
public interface INotificationRecipientResolver
{
    Task<NotificationRecipientSnapshot?> ResolveAsync(NotificationRecipient recipient, CancellationToken cancellationToken = default);
}

/// <summary>Raised to make the outbox retry a delivery later (after a retryable provider failure).</summary>
public sealed class NotificationRetryException(string message) : Exception(message);

/// <summary>A sensitive notification (sign-in link) could not be delivered on any channel; the caller should retry.</summary>
public sealed class NotificationDeliveryException(string message) : Exception(message);
