using Base.Application.Branding;
using Base.Application.Common.Results;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Web.Security;
using Base.Web.Subscriptions;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>This tenant's look: accent colour, default theme and logo.</summary>
[RequiresFeature(FeatureCodes.CustomBranding)]
[Route("admin/appearance")]
[HasPermission(PermissionCodes.Settings.Edit)]
public sealed class AppearanceController(IBrandingService branding) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(await branding.GetAsync(cancellationToken));

    [HttpPost("")]
    public async Task<IActionResult> Save(string primaryColor, string defaultTheme, CancellationToken cancellationToken)
    {
        this.Flash(await branding.SaveAsync(primaryColor, defaultTheme, cancellationToken));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("logo")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2 * 1024 * 1024)]
    public async Task<IActionResult> Logo(IFormFile? logo, CancellationToken cancellationToken)
    {
        if (logo is null)
        {
            this.Flash(ValidationResponse.Failure("Choose an image."));
            return RedirectToAction(nameof(Index));
        }

        await using var stream = logo.OpenReadStream();
        this.Flash(await branding.SetLogoAsync(stream, logo.FileName, cancellationToken));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("logo/remove")]
    public async Task<IActionResult> RemoveLogo(CancellationToken cancellationToken)
    {
        this.Flash(await branding.RemoveLogoAsync(cancellationToken));
        return RedirectToAction(nameof(Index));
    }
}
