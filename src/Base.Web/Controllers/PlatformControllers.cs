using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Domain.Notifications;
using Base.Domain.Subscriptions;
using Base.Web.Results;
using Base.Web.Security;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>The platform's tenants: list, create, and run each one's domains and subscription.</summary>
[Route("platform/tenants")]
[HasPermission(PermissionCodes.Platform.TenantsView)]
public sealed class PlatformTenantsController(ITenantAdministration tenants, ISubscriptionService subscriptions, IPlanAdministration plans, INotificationChannels channels) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? search, SubscriptionStatus? status, int page = 1, CancellationToken cancellationToken = default) =>
        View(new TenantListViewModel(await tenants.SearchAsync(new TenantQuery(search, status, page, PageRequest.DefaultPageSize), cancellationToken), search, status));

    [HttpGet("new")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> New(CancellationToken cancellationToken)
    {
        var offered = await plans.ListAsync(includeRetired: false, cancellationToken);
        return View(new CreateTenantViewModel { Plans = offered, PlanId = offered.Count > 0 ? offered[0].Id : null });
    }

    [HttpPost("new")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> New(CreateTenantViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result = await tenants.CreateAsync(
            new CreateTenantRequest(model.Name, model.Code, model.Host, model.AdminEmail, model.AdminName, model.PlanId, model.BillingCycle, model.WithTrial),
            cancellationToken);
        if (result.IsSuccess)
        {
            var email = await channels.DescribeAsync(NotificationChannel.Email, cancellationToken);
            this.Flash(email.Delivers
                ? result
                : ValidationResponse.Success($"{result.Message} Email isn't being delivered yet, though: set it up under Settings → Email, then resend the welcome from here."));
            return RedirectToAction(nameof(Details), new { id = result.Data });
        }

        result.AddToModelState(ModelState);
        if (result.Errors.Count == 0)
        {
            ModelState.AddModelError(string.Empty, result.Message ?? "The tenant couldn't be created.");
        }

        model.Plans = await plans.ListAsync(includeRetired: false, cancellationToken);
        return View(model);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var result = await tenants.GetAsync(id, cancellationToken);
        return result.IsSuccess
            ? View(new TenantDetailsViewModel(
                result.Data!, await plans.ListAsync(includeRetired: false, cancellationToken), await channels.DescribeAsync(NotificationChannel.Email, cancellationToken)))
            : NotFound();
    }

    [HttpPost("{id:guid}/administrators")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> InviteAdministrator(Guid id, string email, string name, CancellationToken cancellationToken)
    {
        var result = await tenants.InviteAdministratorAsync(id, email, name, cancellationToken);
        this.Flash(result.IsSuccess || result.Errors.Count == 0 ? result : ValidationResponse.Failure(string.Join(" ", result.Errors.Select(e => e.Message))));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/users/{userId:guid}/resend")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> ResendWelcome(Guid id, Guid userId, CancellationToken cancellationToken) =>
        Run(id, tenants.ResendWelcomeAsync(id, userId, cancellationToken));

    [HttpPost("{id:guid}")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> Update(Guid id, string name, string? contactEmail, CancellationToken cancellationToken) =>
        Run(id, tenants.UpdateAsync(id, new UpdateTenantRequest(name, contactEmail), cancellationToken));

    [HttpPost("{id:guid}/active")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> SetActive(Guid id, bool active, CancellationToken cancellationToken) =>
        Run(id, tenants.SetActiveAsync(id, active, cancellationToken));

    [HttpPost("{id:guid}/domains")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> AddDomain(Guid id, string host, CancellationToken cancellationToken) =>
        Run(id, tenants.AddDomainAsync(id, host, cancellationToken));

    [HttpPost("{id:guid}/domains/{domainId:guid}/verify")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> VerifyDomain(Guid id, Guid domainId, CancellationToken cancellationToken) =>
        Run(id, tenants.VerifyDomainAsync(id, domainId, cancellationToken));

    [HttpPost("{id:guid}/domains/{domainId:guid}/remove")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> RemoveDomain(Guid id, Guid domainId, CancellationToken cancellationToken) =>
        Run(id, tenants.RemoveDomainAsync(id, domainId, cancellationToken));

    [HttpPost("{id:guid}/subscription")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> Subscribe(Guid id, Guid planId, BillingCycle billingCycle, bool withTrial, CancellationToken cancellationToken) =>
        Run(id, subscriptions.StartAsync(id, new StartSubscriptionRequest(planId, billingCycle, withTrial), cancellationToken));

    [HttpPost("{id:guid}/subscription/plan")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> ChangePlan(Guid id, Guid planId, BillingCycle billingCycle, CancellationToken cancellationToken) =>
        Run(id, subscriptions.ChangePlanAsync(id, new ChangePlanRequest(planId, billingCycle), cancellationToken));

    [HttpPost("{id:guid}/subscription/payment")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> RecordPayment(Guid id, string? reference, CancellationToken cancellationToken) =>
        Run(id, subscriptions.RecordPaymentAsync(id, reference, cancellationToken));

    [HttpPost("{id:guid}/subscription/extend")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> ExtendTrial(Guid id, int days, CancellationToken cancellationToken) =>
        Run(id, subscriptions.ExtendTrialAsync(id, days, cancellationToken));

    [HttpPost("{id:guid}/subscription/suspend")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> Suspend(Guid id, string reason, CancellationToken cancellationToken) =>
        Run(id, subscriptions.SuspendAsync(id, reason, cancellationToken));

    [HttpPost("{id:guid}/subscription/reinstate")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> Reinstate(Guid id, CancellationToken cancellationToken) =>
        Run(id, subscriptions.ReinstateAsync(id, cancellationToken));

    [HttpPost("{id:guid}/subscription/cancel")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> Cancel(Guid id, bool atPeriodEnd, CancellationToken cancellationToken) =>
        Run(id, subscriptions.CancelAsync(id, atPeriodEnd, cancellationToken));

    [HttpPost("{id:guid}/subscription/resume")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public Task<IActionResult> Resume(Guid id, CancellationToken cancellationToken) =>
        Run(id, subscriptions.ResumeAsync(id, cancellationToken));

    private async Task<IActionResult> Run(Guid id, Task<ValidationResponse> action)
    {
        this.Flash(await action);
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>Plans: prices, trials, features and limits.</summary>
[Route("platform/plans")]
[HasPermission(PermissionCodes.Platform.PlansManage)]
public sealed class PlatformPlansController(IPlanAdministration plans) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(await plans.ListAsync(includeRetired: true, cancellationToken));

    [HttpGet("new")]
    public IActionResult New() => View("Edit", PlanEditViewModel.New());

    [HttpPost("new")]
    public async Task<IActionResult> New(PlanEditViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result = await plans.CreateAsync(model.ToRequest(), cancellationToken);
        if (result.IsSuccess)
        {
            this.Flash(ValidationResponse.Success($"{model.Name} created."));
            return RedirectToAction(nameof(Index));
        }

        result.AddToModelState(ModelState);
        return View("Edit", model);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var result = await plans.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View(PlanEditViewModel.From(result.Data!)) : NotFound();
    }

    [HttpPost("{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, PlanEditViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result = await plans.UpdateAsync(id, model.ToRequest(), cancellationToken);
        if (result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Index));
        }

        result.AddToModelState(ModelState);
        model.Id = id;
        return View(model);
    }

    [HttpPost("{id:guid}/offered")]
    public async Task<IActionResult> SetOffered(Guid id, bool offered, CancellationToken cancellationToken)
    {
        this.Flash(await plans.SetOfferedAsync(id, offered, cancellationToken));
        return RedirectToAction(nameof(Edit), new { id });
    }
}

/// <summary>This organisation's own plan, status and usage. Open to every signed-in user so a suspended tenant can see why.</summary>
[Route("subscription")]
public sealed class SubscriptionController(ISubscriptionService subscriptions, ITenantEntitlements entitlements) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(new SubscriptionPageViewModel(await subscriptions.GetMineAsync(cancellationToken), await entitlements.GetAsync(cancellationToken)));
}

/// <summary>Where every tenant stands on releases: versions in use and how far each gated feature has spread.</summary>
[Route("platform/releases")]
[HasPermission(PermissionCodes.Platform.TenantsView)]
public sealed class PlatformReleasesController(Application.Releases.ITenantReleases releases) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(new PlatformReleasesViewModel(await releases.FleetAsync(cancellationToken)));
}
