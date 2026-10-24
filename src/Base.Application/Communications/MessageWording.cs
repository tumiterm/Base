using Base.Application.Common.Text;
using Base.Application.Notifications;
using Base.Domain.Communications;
using Base.Domain.Notifications;

namespace Base.Application.Communications;

/// <summary>
/// A message's wording as each channel needs it: the email (subject, cleaned HTML and its plain text), SMS, in-app,
/// and WhatsApp (an approved template whose {{1}}, {{2}}... are filled per recipient).
/// </summary>
internal sealed record MessageWording(
    string Title,
    string? Subject,
    string? Html,
    string? Text,
    string? Sms,
    string? WhatsAppTemplateName,
    string? WhatsAppLanguage,
    IReadOnlyList<string> WhatsAppParameters)
{
    private const string WhatsAppVariable = "WhatsApp";

    public static MessageWording From(Broadcast broadcast) => new(
        broadcast.Title,
        broadcast.Subject,
        broadcast.HtmlBody,
        broadcast.TextBody,
        broadcast.SmsBody,
        broadcast.WhatsAppTemplateName,
        broadcast.WhatsAppLanguage,
        MessagingJson.List(broadcast.WhatsAppParameters));

    public static MessageWording From(MessageDraft draft, IRichText richText)
    {
        var html = richText.Sanitize(draft.HtmlBody);
        return new(
            string.IsNullOrWhiteSpace(draft.Title) ? DefaultTitle(draft.Subject) : draft.Title.Trim(),
            Empty(draft.Subject) ? null : draft.Subject!.Trim(),
            html.Length == 0 ? null : html,
            html.Length == 0 ? null : richText.ToPlainText(html),
            Empty(draft.SmsBody) ? null : draft.SmsBody!.Trim(),
            Empty(draft.WhatsAppTemplateName) ? null : draft.WhatsAppTemplateName!.Trim(),
            Empty(draft.WhatsAppLanguage) ? null : draft.WhatsAppLanguage!.Trim(),
            draft.WhatsAppParameters?.Select(p => p ?? string.Empty).ToList() ?? []);
    }

    /// <summary>The SMS: its own text, or the email's plain text.</summary>
    public string? SmsText => Sms ?? Text;

    public NotificationContent ToContent()
    {
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Subject is not null && Html is not null && Text is not null)
        {
            parts[TemplateParts.EmailSubject] = Subject;
            parts[TemplateParts.EmailBody] = Html;
            parts[TemplateParts.EmailText] = Text;
        }

        if (SmsText is { } sms)
        {
            parts[TemplateParts.Sms] = sms;
        }

        if (Text is not null)
        {
            parts[TemplateParts.InAppTitle] = Subject ?? Title;
            parts[TemplateParts.InAppBody] = Text;
        }

        var whatsApp = WhatsAppTemplateName is null
            ? null
            : new WhatsAppTemplate(WhatsAppTemplateName, WhatsAppLanguage ?? "en", WhatsAppParameters.Select((_, i) => WhatsAppVariable + (i + 1)).ToList());
        return new NotificationContent(parts, whatsApp);
    }

    /// <summary>The recipient's placeholder values, plus the WhatsApp parameters filled from them.</summary>
    public Dictionary<string, string?> ValuesFor(AudienceMember member)
    {
        var values = new Dictionary<string, string?>(member.AllValues(), StringComparer.Ordinal);
        for (var i = 0; i < WhatsAppParameters.Count; i++)
        {
            values[WhatsAppVariable + (i + 1)] = TemplateRenderer.Render(WhatsAppParameters[i], values, html: false);
        }

        return values;
    }

    /// <summary>Why it can't go out on these channels with this audience's placeholders; empty when it can.</summary>
    public IReadOnlyList<(string Field, string Message)> Problems(IReadOnlyList<NotificationChannel> channels, IReadOnlySet<string> placeholders)
    {
        var problems = new List<(string, string)>();
        if (channels.Count == 0)
        {
            problems.Add((nameof(MessageDraft.Channels), "Choose at least one way to send it."));
        }

        if (channels.Contains(NotificationChannel.Email) && (Subject is null || Html is null))
        {
            problems.Add((Subject is null ? nameof(MessageDraft.Subject) : nameof(MessageDraft.HtmlBody), "An email needs a subject and a message."));
        }

        if (channels.Contains(NotificationChannel.InApp) && Text is null)
        {
            problems.Add((nameof(MessageDraft.HtmlBody), "An in-app message needs a message."));
        }

        if (channels.Contains(NotificationChannel.Sms) && SmsText is null)
        {
            problems.Add((nameof(MessageDraft.SmsBody), "An SMS needs text: write one, or write the message and it is used."));
        }

        if (channels.Contains(NotificationChannel.WhatsApp) && WhatsAppTemplateName is null)
        {
            problems.Add((nameof(MessageDraft.WhatsAppTemplateName), "WhatsApp needs the name of a template Meta has approved."));
        }

        var used = new[] { Subject, Html, SmsText }.Concat(WhatsAppParameters)
            .Where(t => t is not null)
            .SelectMany(t => TemplateRenderer.VariablesIn(t!))
            .Where(v => !placeholders.Contains(v) && !NotificationCatalog.BuiltInVariables.Contains(v, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (used.Count > 0)
        {
            problems.Add((nameof(MessageDraft.HtmlBody), $"{string.Join(", ", used.Select(u => "{{" + u + "}}"))} isn't available for this audience. Use one of: {string.Join(", ", placeholders.Order(StringComparer.Ordinal).Select(p => "{{" + p + "}}"))}."));
        }

        return problems;
    }

    /// <summary>SMS parts: 160 characters (153 each when split) in the GSM alphabet, otherwise 70 (67).</summary>
    public static int SmsParts(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var gsm = text.All(c => Gsm.Contains(c));
        var single = gsm ? 160 : 70;
        var multi = gsm ? 153 : 67;
        return text.Length <= single ? 1 : (text.Length + multi - 1) / multi;
    }

    private const string Gsm = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    private static bool Empty(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>The subject with placeholders shown as "…": "Payslips for {{FirstName}}" is listed as "Payslips for …".</summary>
    private static string DefaultTitle(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return "Message";
        }

        var title = subject.Trim();
        foreach (var name in TemplateRenderer.VariablesIn(title))
        {
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\{\{\s*" + name + @"\s*\}\}", "…", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        }

        return title;
    }
}

internal static class MessagingJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Write<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string? json) => string.IsNullOrWhiteSpace(json) ? default : System.Text.Json.JsonSerializer.Deserialize<T>(json, Options);

    public static IReadOnlyList<string> List(string? json) => Read<List<string>>(json) ?? [];
}
