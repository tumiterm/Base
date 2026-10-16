using Base.Web.Errors;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>Target of exception and status-code re-execution. Any verb, no antiforgery: it must always render.</summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("error")]
public sealed class ErrorController : Controller
{
    [Route("")]
    public IActionResult Index() =>
        View("Error", new ErrorViewModel(
            StatusCodes.Status500InternalServerError,
            HttpContext.Items[GlobalExceptionHandler.TicketItemKey] as string));

    [Route("{statusCode:int}")]
    public IActionResult Status(int statusCode)
    {
        var original = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        if (original?.OriginalPath.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Problem(statusCode: statusCode);
        }

        Response.StatusCode = statusCode;
        return View("Error", new ErrorViewModel(statusCode, null));
    }
}
