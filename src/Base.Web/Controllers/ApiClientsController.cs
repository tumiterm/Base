using Base.Application.Common.Abstractions;
using Base.Application.Integrations;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Web.Results;
using Base.Web.Security;
using Base.Web.Subscriptions;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>API keys for other systems. A new or rotated key is shown once, in the response to the post; never stored.</summary>
[Route("admin/api-clients")]
[HasPermission(PermissionCodes.Api.ClientsManage)]
[RequiresFeature(FeatureCodes.Api)]
public sealed class ApiClientsController(
    IApiClientAdministration clients,
    ICurrentUser currentUser,
    IPlatformTenant platform,
    IApplicationSettingService settings) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(new ApiClientListViewModel(await clients.ListAsync(cancellationToken), await FormAsync(new ApiClientForm(), cancellationToken)));

    [HttpPost("")]
    public async Task<IActionResult> Create(ApiClientForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var result = await clients.CreateAsync(form.ToRequest(), cancellationToken);
        if (result.IsSuccess)
        {
            return KeyView(result.Data!, form.Name, created: true);
        }

        result.AddToModelState(ModelState);
        return View("Index", new ApiClientListViewModel(await clients.ListAsync(cancellationToken), await FormAsync(form, cancellationToken), OpenForm: true));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var client = await clients.GetAsync(id, cancellationToken);
        return client.IsSuccess ? View(new ApiClientDetailsViewModel(client.Data!, await FormAsync(ApiClientForm.From(client.Data!), cancellationToken))) : NotFound();
    }

    [HttpPost("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, ApiClientForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var result = await clients.UpdateAsync(id, form.ToRequest(), cancellationToken);
        if (result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Details), new { id });
        }

        var client = await clients.GetAsync(id, cancellationToken);
        if (!client.IsSuccess)
        {
            return NotFound();
        }

        result.AddToModelState(ModelState);
        if (result.Errors.Count == 0)
        {
            ModelState.AddModelError(string.Empty, result.Message ?? "The changes couldn't be saved.");
        }

        return View(new ApiClientDetailsViewModel(client.Data!, await FormAsync(form, cancellationToken)));
    }

    [HttpPost("{id:guid}/rotate")]
    public async Task<IActionResult> Rotate(Guid id, int expiresInDays, CancellationToken cancellationToken)
    {
        var result = await clients.RotateAsync(id, expiresInDays, cancellationToken);
        if (!result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Details), new { id });
        }

        var client = await clients.GetAsync(id, cancellationToken);
        return KeyView(result.Data!, client.Data?.Name ?? "API client", created: false);
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        this.Flash(await clients.RevokeAsync(id, cancellationToken));
        return RedirectToAction(nameof(Details), new { id });
    }

    private ViewResult KeyView(IssuedApiKey key, string name, bool created)
    {
        // The key is in this response only: never cached, never in a URL, never in TempData.
        Response.Headers.CacheControl = "no-store";
        return View("Key", new ApiKeyViewModel(key, name, created));
    }

    private async Task<ApiClientForm> FormAsync(ApiClientForm form, CancellationToken cancellationToken)
    {
        var isPlatform = await platform.IsCurrentAsync(cancellationToken);
        form.Grantable = PermissionCatalog.All.Where(p => currentUser.HasPermission(p.Key) && (isPlatform || !p.IsPlatform)).ToList();
        form.MaxLifetimeDays = await settings.GetIntAsync(SettingCodes.Api.KeyLifetimeDays, cancellationToken);
        form.DefaultRequestsPerMinute = await settings.GetIntAsync(SettingCodes.Api.DefaultRequestsPerMinute, cancellationToken);
        return form;
    }
}
