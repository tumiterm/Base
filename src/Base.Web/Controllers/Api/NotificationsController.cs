using Base.Application.Common.Persistence;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Domain.Notifications;
using Base.Web.Results;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

public sealed record UpdatePreferenceRequest(string Category, NotificationChannel Channel, bool IsEnabled);

public sealed record SaveTemplatePartRequest(string Content);

/// <summary>The signed-in user's in-app inbox and notification preferences.</summary>
[ApiController]
[Route("api/me/notifications")]
public sealed class MyNotificationsController(INotificationInbox inbox, INotificationPreferences preferences) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<InboxItem>> Inbox([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        inbox.GetAsync(new PageRequest(page, pageSize), cancellationToken);

    [HttpGet("unread-count")]
    public async Task<int> UnreadCount(CancellationToken cancellationToken) => await inbox.UnreadCountAsync(cancellationToken);

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken) =>
        (await inbox.MarkReadAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost("read-all")]
    public async Task<int> MarkAllRead(CancellationToken cancellationToken) => await inbox.MarkAllReadAsync(cancellationToken);

    [HttpGet("preferences")]
    public Task<IReadOnlyList<NotificationPreferenceItem>> Preferences(CancellationToken cancellationToken) => preferences.GetMineAsync(cancellationToken);

    [HttpPut("preferences")]
    public async Task<IActionResult> SetPreference(UpdatePreferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await preferences.SetMineAsync(request.Category, request.Channel, request.IsEnabled, cancellationToken)).ToActionResult(this);
    }
}

/// <summary>Administration: the delivery log, resends, and this tenant's template wording.</summary>
[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController(INotificationLog log, INotificationTemplateAdmin templates) : ControllerBase
{
    [HttpGet]
    [HasPermission(PermissionCodes.Notifications.View)]
    public Task<PagedResult<NotificationLogItem>> Search(
        [FromQuery] string? search,
        [FromQuery] NotificationChannel? channel,
        [FromQuery] NotificationStatus? status,
        [FromQuery] string? template,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        log.SearchAsync(new NotificationLogQuery(search, channel, status, template, page, pageSize), cancellationToken);

    [HttpPost("{id:guid}/resend")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> Resend(Guid id, CancellationToken cancellationToken)
    {
        var result = await log.ResendAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(new { id = result.Data }) : result.ToActionResult(this);
    }

    [HttpGet("templates")]
    [HasPermission(PermissionCodes.Notifications.View)]
    public Task<IReadOnlyList<NotificationTemplateView>> Templates(CancellationToken cancellationToken) => templates.ListAsync(cancellationToken);

    [HttpPut("templates/{code}/{part}")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> SaveTemplatePart(string code, string part, SaveTemplatePartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await templates.SaveOverrideAsync(code, part, request.Content, cancellationToken)).ToActionResult(this);
    }

    [HttpDelete("templates/{code}/{part}")]
    [HasPermission(PermissionCodes.Notifications.Manage)]
    public async Task<IActionResult> ResetTemplatePart(string code, string part, CancellationToken cancellationToken) =>
        (await templates.ResetOverrideAsync(code, part, cancellationToken)).ToActionResult(this);
}
