using Base.Application.Common.Abstractions;
using Base.Application.Releases;
using Base.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.ViewComponents;

/// <summary>Tells the people who take releases that new features are waiting (not on the What's new page itself).</summary>
public sealed class ReleaseNoticeViewComponent(ITenantReleases releases, IReleaseCatalog catalog, ICurrentUser user) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (catalog.Features.Count == 0 || !user.HasPermission(PermissionCodes.Releases.Manage)
            || string.Equals(ViewContext.RouteData.Values["controller"] as string, "Releases", StringComparison.Ordinal))
        {
            return Content(string.Empty);
        }

        var state = await releases.GetAsync(HttpContext.RequestAborted);
        return state.IsUpToDate ? Content(string.Empty) : View(state);
    }
}
