using System.Net;
using System.Text;
using Base.Application.Common.Results;
using Base.Application.Notifications;

namespace Base.Web.Formatting;

/// <summary>
/// The page the template editor's preview frame shows: the email as it will arrive, or (for a template without
/// email) its SMS and in-app wording, or what is wrong with the draft. Served sandboxed, under the framed policy.
/// </summary>
public static class TemplatePreviewPage
{
    public static string Html(ValidationResponse<TemplatePreview> preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.IsError)
        {
            var problems = preview.Errors.Count > 0 ? preview.Errors.Select(e => e.Message) : [preview.Message ?? "The preview couldn't be made."];
            return Page("<p style=\"color:#b42318;font-weight:bold\">Fix this to see the preview:</p><ul>" + string.Concat(problems.Select(p => "<li>" + E(p) + "</li>")) + "</ul>");
        }

        var p = preview.Data!;
        if (p.EmailHtml is { } html)
        {
            return html;
        }

        var body = new StringBuilder();
        if (p.InAppTitle is not null || p.InAppBody is not null)
        {
            body.Append("<p style=\"color:#5b6470;font-size:12px;text-transform:uppercase;letter-spacing:.06em\">In-app</p><div style=\"background:#fff;border-radius:8px;padding:16px\"><strong>")
                .Append(E(p.InAppTitle ?? string.Empty)).Append("</strong><div style=\"white-space:pre-line\">").Append(E(p.InAppBody ?? string.Empty)).Append("</div></div>");
        }

        if (p.Sms is { } sms)
        {
            body.Append("<p style=\"color:#5b6470;font-size:12px;text-transform:uppercase;letter-spacing:.06em\">SMS</p><div style=\"background:#fff;border-radius:16px 16px 16px 4px;padding:12px 16px;white-space:pre-line;display:inline-block\">")
                .Append(E(sms)).Append("</div>");
        }

        return Page(body.Length == 0 ? "<p>Nothing to show.</p>" : body.ToString());
    }

    private static string Page(string body) =>
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Preview</title></head><body style=\"margin:0;padding:24px;background:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1a1a1a;font-size:15px;line-height:1.5\">"
        + body + "</body></html>";

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
