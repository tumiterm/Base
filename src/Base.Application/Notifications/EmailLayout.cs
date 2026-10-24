using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Base.Application.Notifications;

/// <summary>One line of an email's details table; <see cref="IsTotal"/> sets it apart (a total paid).</summary>
public sealed record EmailDetail(string Label, string Value, bool IsTotal = false);

/// <summary>An email's parts with the placeholders filled in: plain text everywhere except <see cref="BodyHtml"/>.</summary>
public sealed record EmailParts(
    string Subject,
    string? Preheader,
    string? Heading,
    string BodyHtml,
    IReadOnlyList<EmailDetail> Details,
    string? ButtonLabel,
    string? ButtonUrl)
{
    public bool HasButton => ButtonLabel is not null && ButtonUrl is not null;

    /// <summary>The details as written in a template: <c>Label | Value</c> per line, <c>**</c> first for a total.</summary>
    public static IReadOnlyList<EmailDetail> ParseDetails(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var details = new List<EmailDetail>();
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            var total = line.StartsWith("**", StringComparison.Ordinal);
            if (total)
            {
                line = line[2..].Trim();
            }

            var bar = line.IndexOf('|', StringComparison.Ordinal);
            var label = (bar < 0 ? line : line[..bar]).Trim();
            var value = bar < 0 ? string.Empty : line[(bar + 1)..].Trim();
            if (label.Length > 0 && value.Length > 0)
            {
                details.Add(new EmailDetail(label, value, total));
            }
        }

        return details;
    }
}

/// <summary>Who an email is from, as its frame shows it.</summary>
/// <param name="LogoUrl">An absolute address mail clients can load (inbox images can't be embedded: Gmail drops them).</param>
/// <param name="Accent">The brand colour (<c>#rrggbb</c>) of the button and the top line.</param>
/// <param name="Company">Who sends it, for the footer ("Acme Trading (Pty) Ltd, 12 Long Street, Cape Town").</param>
/// <param name="Reason">Why the person gets it, for the footer.</param>
/// <param name="PreferencesUrl">Where to choose which emails one gets, for messages people can switch off.</param>
public sealed record EmailBrand(
    string Name,
    string? LogoUrl = null,
    string Accent = EmailBrand.DefaultAccent,
    string? Company = null,
    string? Reason = null,
    string? PreferencesUrl = null)
{
    public const string DefaultAccent = "#4f5bd5";

    /// <summary><see cref="Accent"/> when it is a colour, else the default: it goes into style attributes as is.</summary>
    public string Colour => Accent is { Length: 7 } && Accent[0] == '#' && Accent.Skip(1).All(Uri.IsHexDigit) ? Accent : DefaultAccent;

    /// <summary>White or near-black text, whichever reads better on <see cref="Colour"/> (WCAG relative luminance).</summary>
    public string ColourText => Luminance(Colour) > 0.179 ? "#0b0d12" : "#ffffff";

    private static double Luminance(string hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return 0;
        }

        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel((rgb >> 16) & 0xff)) + (0.7152 * Channel((rgb >> 8) & 0xff)) + (0.0722 * Channel(rgb & 0xff));
    }
}

/// <summary>
/// The frame every email is sent in: heading, the message, a table of details, one button (with its address written
/// out for clients that block buttons), and who it is from. Templates hold words; this holds the markup.
/// </summary>
public static partial class EmailLayout
{
    // One palette, light by default; the <style> block swaps it for dark where the client supports it.
    private const string Page = "#f3f4f6";
    private const string Card = "#ffffff";
    private const string Ink = "#111827";
    private const string Muted = "#6b7280";
    private const string Line = "#e5e7eb";
    private const string Panel = "#f9fafb";
    private const string Font = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";

    private const string Head =
        "<style>" +
        ":root{color-scheme:light dark;supported-color-schemes:light dark}" +
        "body{margin:0;padding:0;width:100%!important;-webkit-text-size-adjust:100%}" +
        "a{color:inherit}" +
        "@media (max-width:620px){.email-card{padding:28px 22px!important}.email-pad{padding:0 12px!important}.email-h1{font-size:22px!important}}" +
        "@media (prefers-color-scheme:dark){" +
        ".email-page{background:#0b0d12!important}.email-card{background:#161a22!important}" +
        ".email-ink,.email-ink p,.email-ink li,.email-h1,.email-ink td.email-value{color:#f3f4f6!important}" +
        ".email-muted,.email-muted a,.email-ink .email-muted,.email-ink .email-muted a,.email-ink td.email-label{color:#9ca3af!important}" +
        ".email-panel{background:#1e232d!important}.email-line{border-color:#2a303c!important}.email-ink td.email-total{border-color:#f3f4f6!important}}" +
        "</style>";

    public static string Html(EmailParts email, EmailBrand brand)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(brand);
        var html = new StringBuilder(8192);
        html.Append("<!DOCTYPE html><html lang=\"en\" xmlns=\"http://www.w3.org/1999/xhtml\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"x-apple-disable-message-reformatting\">")
            .Append("<meta name=\"color-scheme\" content=\"light dark\"><meta name=\"supported-color-schemes\" content=\"light dark\">")
            .Append("<title>").Append(E(email.Subject)).Append("</title>").Append(Head).Append("</head>")
            .Append("<body class=\"email-page\" style=\"margin:0;padding:0;background:").Append(Page).Append(";\">");

        // The inbox preview line, padded so the start of the email doesn't run on after it.
        if (email.Preheader is { } preheader)
        {
            html.Append("<div style=\"display:none;font-size:1px;line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;mso-hide:all\">")
                .Append(E(preheader)).Append(string.Concat(Enumerable.Repeat("&#847;&zwnj;&nbsp;", 80))).Append("</div>");
        }

        html.Append("<table role=\"presentation\" class=\"email-page\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background:").Append(Page).Append(";\">")
            .Append("<tr><td align=\"center\" class=\"email-pad\" style=\"padding:32px 16px;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"max-width:600px;\">");

        // Who it's from: the logo, or the name in the brand colour.
        html.Append("<tr><td style=\"padding:0 4px 20px;\">");
        if (brand.LogoUrl is { } logo)
        {
            html.Append("<img src=\"").Append(E(logo)).Append("\" alt=\"").Append(E(brand.Name)).Append("\" height=\"36\" style=\"display:block;height:36px;width:auto;max-width:220px;border:0;outline:none;\">");
        }
        else
        {
            html.Append("<span style=\"font-family:").Append(Font).Append(";font-size:18px;font-weight:700;letter-spacing:-0.01em;color:").Append(brand.Colour).Append(";\">").Append(E(brand.Name)).Append("</span>");
        }

        html.Append("</td></tr>");

        // The card: a line of the brand colour, then the message.
        html.Append("<tr><td style=\"background:").Append(brand.Colour).Append(";height:4px;line-height:4px;font-size:4px;border-radius:12px 12px 0 0;\">&nbsp;</td></tr>")
            .Append("<tr><td class=\"email-card email-ink\" style=\"background:").Append(Card).Append(";border-radius:0 0 12px 12px;padding:40px 40px 36px;font-family:").Append(Font)
            .Append(";font-size:16px;line-height:1.6;color:").Append(Ink).Append(";\">");
        if (email.Heading is { } heading)
        {
            html.Append("<h1 class=\"email-h1\" style=\"margin:0 0 20px;font-family:").Append(Font).Append(";font-size:26px;line-height:1.25;font-weight:700;letter-spacing:-0.02em;color:").Append(Ink).Append(";\">")
                .Append(E(heading)).Append("</h1>");
        }

        html.Append(Styled(email.BodyHtml, brand.Colour));

        if (email.Details.Count > 0)
        {
            html.Append("<table role=\"presentation\" class=\"email-panel\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:24px 0 8px;background:").Append(Panel)
                .Append(";border-radius:10px;\"><tr><td style=\"padding:8px 20px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">");
            for (var i = 0; i < email.Details.Count; i++)
            {
                var d = email.Details[i];
                var border = i == 0 ? "border-top:0;" : d.IsTotal ? "border-top:2px solid " + Ink + ";" : "border-top:1px solid " + Line + ";";
                var size = d.IsTotal ? "font-size:17px;font-weight:700;" : "font-size:15px;";
                var cell = "email-line" + (d.IsTotal ? " email-total" : string.Empty);
                html.Append("<tr><td class=\"").Append(cell).Append(d.IsTotal ? " email-value" : " email-label").Append("\" style=\"padding:12px 12px 12px 0;").Append(border).Append(size)
                    .Append("color:").Append(d.IsTotal ? Ink : Muted).Append(";\">").Append(E(d.Label)).Append("</td>")
                    .Append("<td class=\"").Append(cell).Append(" email-value\" align=\"right\" style=\"padding:12px 0;").Append(border).Append(size).Append("color:").Append(Ink)
                    .Append(";text-align:right;white-space:nowrap;\">").Append(E(d.Value)).Append("</td></tr>");
            }

            html.Append("</table></td></tr></table>");
        }

        // One button, drawn with a table so Outlook shows it too, and its address written out below.
        if (email.HasButton)
        {
            html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:32px 0 8px;\"><tr>")
                .Append("<td align=\"center\" bgcolor=\"").Append(brand.Colour).Append("\" style=\"border-radius:8px;background:").Append(brand.Colour).Append(";\">")
                .Append("<a href=\"").Append(E(email.ButtonUrl!)).Append("\" target=\"_blank\" style=\"display:inline-block;padding:14px 28px;font-family:").Append(Font)
                .Append(";font-size:16px;font-weight:600;line-height:1.2;color:").Append(brand.ColourText).Append(";text-decoration:none;border-radius:8px;\">")
                .Append(E(email.ButtonLabel!)).Append("</a></td></tr></table>")
                .Append("<p class=\"email-muted\" style=\"margin:20px 0 0;font-size:13px;line-height:1.5;color:").Append(Muted).Append(";\">If the button doesn't work, copy this address into your browser:<br>")
                .Append("<a href=\"").Append(E(email.ButtonUrl!)).Append("\" style=\"color:").Append(Muted).Append(";word-break:break-all;\">").Append(E(email.ButtonUrl!)).Append("</a></p>");
        }

        html.Append("</td></tr>");

        // Who sent it, and why.
        html.Append("<tr><td class=\"email-muted\" align=\"center\" style=\"padding:24px 24px 0;font-family:").Append(Font).Append(";font-size:12px;line-height:1.6;color:").Append(Muted).Append(";\">");
        if (brand.Company is { } company)
        {
            html.Append("<p style=\"margin:0 0 6px;\">").Append(E(company)).Append("</p>");
        }

        if (brand.Reason is { } reason)
        {
            html.Append("<p style=\"margin:0;\">").Append(E(reason));
            if (brand.PreferencesUrl is { } preferences)
            {
                html.Append(" <a href=\"").Append(E(preferences)).Append("\" style=\"color:").Append(Muted).Append(";text-decoration:underline;\">Choose which emails you get</a>.");
            }

            html.Append("</p>");
        }

        html.Append("</td></tr></table></td></tr></table></body></html>");
        return html.ToString();
    }

    /// <summary>
    /// The body's simple HTML with its styles written on each element, as mail clients need (many ignore a style
    /// sheet). The body is cleaned HTML and its values are encoded, so its tags are only the allowed ones.
    /// </summary>
    private static string Styled(string body, string colour) =>
        BodyTag().Replace(body, match =>
        {
            var style = match.Groups["tag"].Value.ToLowerInvariant() switch
            {
                "p" => "margin:0 0 16px;",
                "ul" or "ol" => "margin:0 0 16px;padding-left:24px;",
                "li" => "margin:0 0 8px;",
                "h2" => "margin:24px 0 12px;font-size:20px;line-height:1.3;font-weight:700;",
                "h3" => "margin:20px 0 8px;font-size:17px;line-height:1.3;font-weight:700;",
                "blockquote" => "margin:0 0 16px;padding:4px 0 4px 16px;border-left:3px solid " + Line + ";color:" + Muted + ";",
                "a" => "color:" + colour + ";text-decoration:underline;font-weight:600;",
                _ => string.Empty,
            };
            return "<" + match.Groups["tag"].Value + " style=\"" + style + "\"" + match.Groups["rest"].Value;
        });

    [GeneratedRegex(@"<(?<tag>p|ul|ol|li|h2|h3|blockquote|a)(?<rest>(\s[^>]*)?>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BodyTag();

    /// <summary>The plain-text version: <paramref name="bodyText"/> is the body already turned into text.</summary>
    public static string Text(EmailParts email, string bodyText, EmailBrand brand)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(brand);
        var text = new StringBuilder();
        if (email.Heading is { } heading)
        {
            text.Append(heading).Append("\n\n");
        }

        text.Append(bodyText.Trim()).Append('\n');
        if (email.Details.Count > 0)
        {
            text.Append('\n');
            foreach (var d in email.Details)
            {
                text.Append(d.Label).Append(": ").Append(d.Value).Append('\n');
            }
        }

        if (email.HasButton)
        {
            text.Append('\n').Append(email.ButtonLabel).Append(":\n").Append(email.ButtonUrl).Append('\n');
        }

        text.Append("\n-- \n").Append(brand.Company ?? brand.Name).Append('\n');
        if (brand.Reason is { } reason)
        {
            text.Append(reason).Append('\n');
        }

        if (brand.PreferencesUrl is { } preferences)
        {
            text.Append("Choose which emails you get: ").Append(preferences).Append('\n');
        }
        return text.ToString();
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
