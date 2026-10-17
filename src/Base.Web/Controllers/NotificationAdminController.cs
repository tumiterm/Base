using Base.Application.Notifications;
using Base.Application.Security;
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

    [HttpPost("templates/{code}/{part}")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> SavePart(string code, string part, string content, CancellationToken cancellationToken)
    {
        var result = await templates.SaveOverrideAsync(code, part, content, cancellationToken);
        if (!result.IsSuccess)
        {
            result.AddToModelState(ModelState);
            var view = await templates.GetAsync(code, cancellationToken);
            return view.IsSuccess ? View("Template", view.Data) : NotFound();
        }

        this.Flash(result);
        return RedirectToAction(nameof(Template), new { code });
    }

    [HttpPost("templates/{code}/{part}/reset")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> ResetPart(string code, string part, CancellationToken cancellationToken)
    {
        this.Flash(await templates.ResetOverrideAsync(code, part, cancellationToken));
        return RedirectToAction(nameof(Template), new { code });
    }
}
