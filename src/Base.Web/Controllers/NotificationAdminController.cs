using Base.Application.Notifications;
using Base.Application.Security;
using Base.Web.Formatting;
using Base.Web.Http;
using Base.Web.Results;
using Base.Web.Security;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

[Route("admin/notifications")]
[HasPermission(PermissionCodes.Notifications.View)]
public sealed class NotificationAdminController(INotificationLog log, INotificationTemplateAdmin templates) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] NotificationLogFilterViewModel filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var results = await log.SearchAsync(new NotificationLogQuery(filter.Search, filter.Channel, filter.Status, filter.Template, filter.Page, 50), cancellationToken);
        return View(new NotificationLogViewModel(results, filter));
    }

    [HttpPost("{id:guid}/resend")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> Resend(Guid id, CancellationToken cancellationToken)
    {
        var result = await log.ResendAsync(id, cancellationToken);
        this.Flash(result.IsSuccess ? Application.Common.Results.ValidationResponse.Success("Queued again.") : result);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("templates")]
    public async Task<IActionResult> Templates(CancellationToken cancellationToken) => View(await templates.ListAsync(cancellationToken));

    [HttpGet("templates/{code}")]
    public async Task<IActionResult> Template(string code, CancellationToken cancellationToken)
    {
        var result = await templates.GetAsync(code, cancellationToken);
        return result.IsSuccess ? View(result.Data) : NotFound();
    }

    /// <summary>Saves the wording of every part on the page as one new version.</summary>
    [HttpPost("templates/{code}")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> Save(string code, [FromForm] IDictionary<string, string?>? parts, string? note, CancellationToken cancellationToken)
    {
        var result = await templates.SaveAsync(code, Draft(parts), note, cancellationToken);
        if (!result.IsSuccess)
        {
            result.AddToModelState(ModelState);
            var view = await templates.GetAsync(code, cancellationToken);
            if (!view.IsSuccess)
            {
                return NotFound();
            }

            ViewData["Draft"] = parts;
            return View("Template", view.Data);
        }

        this.Flash(result);
        return RedirectToAction(nameof(Template), new { code });
    }

    /// <summary>The template with sample values, for the preview frame: as saved (GET) or as on the page (POST).</summary>
    [HttpGet("templates/{code}/preview")]
    public Task<IActionResult> Preview(string code, CancellationToken cancellationToken) => PreviewAsync(code, null, cancellationToken);

    [HttpPost("templates/{code}/preview")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public Task<IActionResult> PreviewDraft(string code, [FromForm] IDictionary<string, string?>? parts, CancellationToken cancellationToken) =>
        PreviewAsync(code, Draft(parts), cancellationToken);

    [HttpPost("templates/{code}/test")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> SendTest(string code, [FromForm] IDictionary<string, string?>? parts, CancellationToken cancellationToken)
    {
        var result = await templates.SendTestAsync(code, Draft(parts), cancellationToken);
        this.Flash(result.IsSuccess || result.Errors.Count == 0 ? result : Application.Common.Results.ValidationResponse.Failure(string.Join(" ", result.Errors.Select(e => e.Message))));
        return RedirectToAction(nameof(Template), new { code });
    }

    [HttpPost("templates/{code}/reset")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> Reset(string code, CancellationToken cancellationToken)
    {
        this.Flash(await templates.ResetAsync(code, cancellationToken));
        return RedirectToAction(nameof(Template), new { code });
    }

    [HttpPost("templates/{code}/versions/{id:guid}/restore")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> Restore(string code, Guid id, CancellationToken cancellationToken)
    {
        this.Flash(await templates.RestoreAsync(code, id, cancellationToken));
        return RedirectToAction(nameof(Template), new { code });
    }

    private static Dictionary<string, string?> Draft(IDictionary<string, string?>? parts) =>
        parts is null ? new(StringComparer.Ordinal) : new(parts, StringComparer.Ordinal);

    private async Task<IActionResult> PreviewAsync(string code, IReadOnlyDictionary<string, string?>? draft, CancellationToken cancellationToken)
    {
        var preview = await templates.PreviewAsync(code, draft, cancellationToken);
        if (preview.ErrorType == Application.Common.Results.ErrorType.NotFound)
        {
            return NotFound();
        }

        Response.AllowFramedPreview(sandboxed: true);
        Response.Headers.CacheControl = "no-store";
        return Content(TemplatePreviewPage.Html(preview), "text/html; charset=utf-8");
    }
}
