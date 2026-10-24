using Base.Domain.Notifications;

namespace Base.Application.Notifications;

/// <summary>
/// The named sections of a template file. Each channel needs its parts to be sendable on that channel. An email is
/// built from parts (heading, body, details, button) that the shared email layout lays out, so templates hold words,
/// not markup.
/// </summary>
public static class TemplateParts
{
    public const string EmailSubject = "email.subject";

    /// <summary>The line inboxes show after the subject.</summary>
    public const string EmailPreheader = "email.preheader";

    public const string EmailHeading = "email.heading";

    /// <summary>The message itself: simple HTML (paragraphs, bold, lists, links).</summary>
    public const string EmailBody = "email.body";

    /// <summary>Facts set out as a table, one <c>Label | Value</c> per line; a line starting <c>**</c> is the total.</summary>
    public const string EmailDetails = "email.details";

    /// <summary>The button's label; <see cref="EmailAction"/> is where it goes.</summary>
    public const string EmailButton = "email.button";

    /// <summary>The button's address. Set by the application, never edited (a link must go where it says).</summary>
    public const string EmailAction = "email.action";

    /// <summary>The plain-text email. Optional: without it the text is made from the other parts.</summary>
    public const string EmailText = "email.text";

    /// <summary>Older files' whole HTML body; read as <see cref="EmailBody"/>.</summary>
    public const string EmailHtml = "email.html";

    public const string Sms = "sms";
    public const string InAppTitle = "inapp.title";
    public const string InAppBody = "inapp.body";

    public static IReadOnlyList<string> All { get; } =
        [EmailSubject, EmailPreheader, EmailHeading, EmailBody, EmailDetails, EmailButton, EmailAction, EmailText, EmailHtml, Sms, InAppTitle, InAppBody];

    /// <summary>What an organisation (or the platform) may reword. Addresses, details and text versions follow the code.</summary>
    public static IReadOnlyList<string> Editable { get; } = [EmailSubject, EmailPreheader, EmailHeading, EmailBody, EmailButton, Sms, InAppTitle, InAppBody];

    /// <summary>HTML parts are HTML-encoded when rendered; the rest are plain text (the layout encodes them).</summary>
    public static bool IsHtml(string part) => string.Equals(part, EmailBody, StringComparison.Ordinal) || string.Equals(part, EmailHtml, StringComparison.Ordinal);

    public static bool IsEditable(string part) => Editable.Contains(part, StringComparer.Ordinal);

    public static IReadOnlyList<string> RequiredFor(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => [EmailSubject, EmailBody],
        NotificationChannel.Sms => [Sms],
        NotificationChannel.InApp => [InAppTitle, InAppBody],
        _ => [], // WhatsApp sends an approved template, not text
    };
}
