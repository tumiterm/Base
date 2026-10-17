using Base.Application.Common.Abstractions;
using Base.Application.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.ViewComponents;

/// <summary>The navbar bell with the unread in-app count. Server-rendered on each page; no polling script.</summary>
public sealed class NotificationBellViewComponent(INotificationInbox inbox, ICurrentUser currentUser) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync() =>
        currentUser.IsAuthenticated ? View(await inbox.UnreadCountAsync(HttpContext.RequestAborted)) : Content(string.Empty);
}
