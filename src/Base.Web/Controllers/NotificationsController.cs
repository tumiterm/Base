using Base.Application.Common.Persistence;
using Base.Application.Notifications;
using Base.Domain.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>The signed-in user's in-app inbox and preferences.</summary>
[Route("notifications")]
public sealed class NotificationsController(INotificationInbox inbox, INotificationPreferences preferences) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, CancellationToken cancellationToken = default) =>
        View(await inbox.GetAsync(new PageRequest(page, 20), cancellationToken));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        await inbox.MarkReadAsync(id, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await inbox.MarkAllReadAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("preferences")]
    public async Task<IActionResult> Preferences(CancellationToken cancellationToken) =>
        View(await preferences.GetMineAsync(cancellationToken));

    [HttpPost("preferences")]
    public async Task<IActionResult> Preferences(string category, NotificationChannel channel, bool isEnabled, CancellationToken cancellationToken)
    {
        this.Flash(await preferences.SetMineAsync(category, channel, isEnabled, cancellationToken));
        return RedirectToAction(nameof(Preferences));
    }
}
