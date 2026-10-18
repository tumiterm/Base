using Base.Application.Common.Results;
using Base.Application.Users;
using Base.Web.Results;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>
/// Answers a request for an invitation link. The copy button asks for JSON and puts the link on the clipboard; without
/// script (or when the browser blocks the clipboard) the link is shown on a page instead. Neither may be cached.
/// </summary>
internal static class InviteLinkResults
{
    public static IActionResult InviteLinkResult(this Controller controller, ValidationResponse<InviteLink> result, string backUrl)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);
        controller.Response.Headers.CacheControl = "no-store";
        if (controller.Request.Headers.Accept.Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            return result.ToActionResult(controller);
        }

        if (result.IsError)
        {
            controller.Flash(result);
            return controller.LocalRedirect(backUrl);
        }

        return controller.View("InviteLink", new InviteLinkViewModel(result.Data!, backUrl));
    }
}
