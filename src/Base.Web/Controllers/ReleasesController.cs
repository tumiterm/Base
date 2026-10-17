using Base.Application.Releases;
using Base.Application.Security;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>"What's new": the organisation's release, what is waiting, and trying or keeping features (ADR 0022).</summary>
[Route("admin/releases")]
[HasPermission(PermissionCodes.Releases.Manage)]
public sealed class ReleasesController(ITenantReleases releases, IReleaseCatalog catalog) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(new ViewModels.ReleasesPageViewModel(await releases.GetAsync(cancellationToken), catalog.Features.Count));

    [HttpPost("adopt")]
    public async Task<IActionResult> Adopt(string? version, CancellationToken cancellationToken)
    {
        this.Flash(await releases.AdoptAsync(version, cancellationToken));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("features/{code}")]
    public async Task<IActionResult> Choose(string code, bool isOn, CancellationToken cancellationToken)
    {
        this.Flash(await releases.ChooseAsync(code, isOn, cancellationToken));
        return Redirect(Url.Action(nameof(Index)) + "#feature-" + code.Replace('.', '-'));
    }

    [HttpPost("features/{code}/follow")]
    public async Task<IActionResult> Follow(string code, CancellationToken cancellationToken)
    {
        this.Flash(await releases.ClearChoiceAsync(code, cancellationToken));
        return RedirectToAction(nameof(Index));
    }
}
