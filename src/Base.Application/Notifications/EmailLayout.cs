using System.Net;
using System.Text;

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
public static class EmailLayout
{
    public static string Html(EmailParts email, EmailBrand brand)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(brand);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(E(email.Subject)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:24px;background:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1a1a1a;font-size:15px;line-height:1.5\">");
        if (email.Preheader is { } preheader)
        {
            html.Append("<div style=\"display:none;max-height:0;overflow:hidden\">").Append(E(preheader)).Append("</div>");
        }

        html.Append("<div style=\"max-width:600px;margin:0 auto;background:#ffffff;border-radius:8px;padding:32px\">")
            .Append("<div style=\"border-top:4px solid ").Append(brand.Colour).Append(";margin:-32px -32px 24px;border-radius:8px 8px 0 0\"></div>");
        if (brand.LogoUrl is { } logo)
        {
            html.Append("<img src=\"").Append(E(logo)).Append("\" alt=\"").Append(E(brand.Name)).Append("\" height=\"40\" style=\"display:block;height:40px;max-width:200px;margin-bottom:16px\">");
        }
        else
        {
            html.Append("<div style=\"font-weight:bold;color:#5b6470;margin-bottom:16px\">").Append(E(brand.Name)).Append("</div>");
        }
        if (email.Heading is { } heading)
        {
            html.Append("<h1 style=\"font-size:22px;margin:0 0 16px\">").Append(E(heading)).Append("</h1>");
        }

        html.Append(email.BodyHtml);
        if (email.Details.Count > 0)
        {
            html.Append("<table role=\"presentation\" style=\"width:100%;border-collapse:collapse;margin:16px 0\">");
            foreach (var d in email.Details)
            {
                var weight = d.IsTotal ? "font-weight:bold;border-top:2px solid #1a1a1a;" : "border-top:1px solid #e3e6ea;";
                html.Append("<tr><td style=\"padding:8px 0;").Append(weight).Append("color:").Append(d.IsTotal ? "#1a1a1a" : "#5b6470").Append("\">").Append(E(d.Label))
                    .Append("</td><td style=\"padding:8px 0;text-align:right;").Append(weight).Append("\">").Append(E(d.Value)).Append("</td></tr>");
            }

            html.Append("</table>");
        }

        if (email.HasButton)
        {
            html.Append("<p style=\"margin:24px 0\"><a href=\"").Append(E(email.ButtonUrl!)).Append("\" style=\"background:").Append(brand.Colour).Append(";color:").Append(brand.ColourText).Append(";padding:12px 20px;border-radius:6px;text-decoration:none;font-weight:bold;display:inline-block\">")
                .Append(E(email.ButtonLabel!)).Append("</a></p>")
                .Append("<p style=\"color:#5b6470;font-size:12px\">If the button doesn't work, copy this address into your browser:<br>").Append(E(email.ButtonUrl!)).Append("</p>");
        }

        html.Append("</div><div style=\"max-width:600px;margin:16px auto 0;color:#5b6470;font-size:12px;text-align:center\">");
        if (brand.Company is { } company)
        {
            html.Append("<p style=\"margin:0 0 4px\">").Append(E(company)).Append("</p>");
        }

        if (brand.Reason is { } reason)
        {
            html.Append("<p style=\"margin:0\">").Append(E(reason));
            if (brand.PreferencesUrl is { } preferences)
            {
                html.Append(" <a href=\"").Append(E(preferences)).Append("\" style=\"color:#5b6470\">Choose which emails you get</a>.");
            }

            html.Append("</p>");
        }

        html.Append("</div></body></html>");
        return html.ToString();
    }

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
