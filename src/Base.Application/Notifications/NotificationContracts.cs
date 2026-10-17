using System.Globalization;
using System.Reflection;
using Base.Domain.Notifications;

namespace Base.Application.Notifications;

/// <summary>
/// A request to notify someone. <paramref name="Data"/> is an object or dictionary whose members fill the template's
/// variables. <paramref name="Channels"/> overrides the template's default channels.
/// </summary>
public sealed record Notification(string Template, NotificationRecipient Recipient, object? Data = null, IReadOnlyList<NotificationChannel>? Channels = null);

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

internal static class NotificationData
{
    /// <summary>Turns a dictionary or an object's public properties into template values (invariant formatting).</summary>
    public static Dictionary<string, string?> From(object? data)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        switch (data)
        {
            case null:
                break;
            case IEnumerable<KeyValuePair<string, string?>> strings:
                foreach (var (key, value) in strings)
                {
                    values[key] = value;
                }

                break;
            case IEnumerable<KeyValuePair<string, object?>> objects:
                foreach (var (key, value) in objects)
                {
                    values[key] = Format(value);
                }

                break;
            default:
                foreach (var property in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
                {
                    values[property.Name] = Format(property.GetValue(data));
                }

                break;
        }

        return values;
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
