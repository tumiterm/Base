using Base.Application.Notifications;
using Base.Domain.Notifications;

namespace Base.Web.Formatting;

/// <summary>How notification channels, statuses, templates and template parts are shown.</summary>
public static class MessageDisplay
{
    public static string Icon(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => "mail",
        NotificationChannel.Sms => "phone",
        NotificationChannel.WhatsApp => "send",
        _ => "bell",
    };

    public static string Channel(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Sms => "SMS",
        NotificationChannel.WhatsApp => "WhatsApp",
        NotificationChannel.InApp => "In-app",
        _ => "Email",
    };

    public static (string Label, string Tone) Status(NotificationStatus status) => status switch
    {
        NotificationStatus.Sent => ("Sent", "success"),
        NotificationStatus.Failed => ("Failed", "danger"),
        NotificationStatus.Skipped => ("Skipped", "secondary"),
        NotificationStatus.Retrying => ("Retrying", "warning"),
        _ => ("Queued", "info"),
    };

    public static (string Label, string Tone, string Icon) Broadcast(Domain.Communications.BroadcastStatus status) => status switch
    {
        Domain.Communications.BroadcastStatus.Scheduled => ("Scheduled", "info", "clock"),
        Domain.Communications.BroadcastStatus.Sending => ("Sending", "warning", "send"),
        Domain.Communications.BroadcastStatus.Sent => ("Sent", "success", "circle-check"),
        Domain.Communications.BroadcastStatus.Cancelled => ("Cancelled", "secondary", "ban"),
        _ => ("Not sent", "danger", "circle-alert"),
    };

    public static string Template(string code) => NotificationCatalog.Find(code)?.Name ?? code;

    /// <summary>"email.subject" → "Email subject", "inapp.body" → "In-app message".</summary>
    public static string Part(string part) => part switch
    {
        "email.subject" => "Email subject",
        "email.html" => "Email body (HTML)",
        "email.text" => "Email body (plain text)",
        "sms" => "SMS",
        "inapp.title" => "In-app title",
        "inapp.body" => "In-app message",
        _ => part,
    };
}
