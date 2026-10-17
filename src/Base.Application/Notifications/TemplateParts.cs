using Base.Domain.Notifications;

namespace Base.Application.Notifications;

/// <summary>The named sections of a template file. Each channel needs its parts to be sendable on that channel.</summary>
public static class TemplateParts
{
    public const string EmailSubject = "email.subject";
    public const string EmailHtml = "email.html";
    public const string EmailText = "email.text";
    public const string Sms = "sms";
    public const string InAppTitle = "inapp.title";
    public const string InAppBody = "inapp.body";

    public static IReadOnlyList<string> All { get; } = [EmailSubject, EmailHtml, EmailText, Sms, InAppTitle, InAppBody];

    /// <summary>HTML parts are HTML-encoded when rendered; the rest are plain text.</summary>
    public static bool IsHtml(string part) => string.Equals(part, EmailHtml, StringComparison.Ordinal);

    public static IReadOnlyList<string> RequiredFor(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => [EmailSubject, EmailHtml, EmailText],
        NotificationChannel.Sms => [Sms],
        NotificationChannel.InApp => [InAppTitle, InAppBody],
        _ => [], // WhatsApp sends an approved template, not text
    };
}
