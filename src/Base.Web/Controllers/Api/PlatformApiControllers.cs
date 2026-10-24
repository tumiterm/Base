using Base.Application.Common.Persistence;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Domain.Subscriptions;
using Base.Web.Results;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

[ApiController]
[Route("api/platform/tenants")]
[HasPermission(PermissionCodes.Platform.TenantsView)]
public sealed class TenantsApiController(ITenantAdministration tenants, ISubscriptionService subscriptions) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<TenantSummary>> Search([FromQuery] string? search, [FromQuery] SubscriptionStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = PageRequest.DefaultPageSize, CancellationToken cancellationToken = default) =>
        tenants.SearchAsync(new TenantQuery(search, status, page, pageSize), cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => (await tenants.GetAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> Create(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        var result = await tenants.CreateAsync(request, cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Data }, new { id = result.Data, message = result.Message }) : result.ToActionResult(this);
    }

    [HttpPut("{id:guid}")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> Update(Guid id, UpdateTenantRequest request, CancellationToken cancellationToken) =>
        (await tenants.UpdateAsync(id, request, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/active")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> SetActive(Guid id, [FromQuery] bool active, CancellationToken cancellationToken) =>
        (await tenants.SetActiveAsync(id, active, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/domains")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> AddDomain(Guid id, DomainRequest request, CancellationToken cancellationToken) =>
        (await tenants.AddDomainAsync(id, request?.Host ?? string.Empty, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/domains/{domainId:guid}/verify")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> VerifyDomain(Guid id, Guid domainId, CancellationToken cancellationToken) =>
        (await tenants.VerifyDomainAsync(id, domainId, cancellationToken)).ToActionResult(this);

    [HttpDelete("{id:guid}/domains/{domainId:guid}")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> RemoveDomain(Guid id, Guid domainId, CancellationToken cancellationToken) =>
        (await tenants.RemoveDomainAsync(id, domainId, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/subscription")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> Subscribe(Guid id, StartSubscriptionRequest request, CancellationToken cancellationToken) =>
        (await subscriptions.StartAsync(id, request, cancellationToken)).ToActionResult(this);

    /// <summary>Changes the plan now or at the end of the period, with a reason (the tenant is told).</summary>
    [HttpPut("{id:guid}/subscription/plan")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> ChangePlan(Guid id, ChangePlanRequest request, CancellationToken cancellationToken) =>
        (await subscriptions.ChangePlanAsync(id, request, cancellationToken)).ToActionResult(this);

    /// <summary>What changing to a plan and cycle would do: when, what is due, limits it is over, features gained and lost.</summary>
    [HttpGet("{id:guid}/subscription/plan/preview")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> PreviewPlanChange(Guid id, Guid planId, BillingCycle billingCycle, CancellationToken cancellationToken) =>
        (await subscriptions.PreviewChangeAsync(id, planId, billingCycle, cancellationToken)).ToActionResult(this);

    /// <summary>Withdraws a change waiting for the end of the period.</summary>
    [HttpDelete("{id:guid}/subscription/plan/scheduled")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> WithdrawPlanChange(Guid id, CancellationToken cancellationToken) =>
        (await subscriptions.CancelScheduledChangeAsync(id, cancellationToken)).ToActionResult(this);

    /// <summary>For a billing integration: a payment for the next period, with the billing system's reference.</summary>
    [HttpPost("{id:guid}/subscription/payments")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> RecordPayment(Guid id, PaymentRequest? request, CancellationToken cancellationToken) =>
        (await subscriptions.RecordPaymentAsync(id, request?.Reference, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/subscription/suspend")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> Suspend(Guid id, SuspendRequest request, CancellationToken cancellationToken) =>
        (await subscriptions.SuspendAsync(id, request?.Reason ?? string.Empty, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/subscription/reinstate")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> Reinstate(Guid id, CancellationToken cancellationToken) =>
        (await subscriptions.ReinstateAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/subscription/cancel")]
    [HasPermission(PermissionCodes.Platform.TenantsManage)]
    public async Task<IActionResult> Cancel(Guid id, [FromQuery] bool atPeriodEnd = true, CancellationToken cancellationToken = default) =>
        (await subscriptions.CancelAsync(id, atPeriodEnd, cancellationToken)).ToActionResult(this);

    public sealed record DomainRequest(string Host);

    public sealed record PaymentRequest(string? Reference);

    public sealed record SuspendRequest(string Reason);
}

[ApiController]
[Route("api/platform/plans")]
[HasPermission(PermissionCodes.Platform.PlansManage)]
public sealed class PlansApiController(IPlanAdministration plans) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<PlanSummary>> List(CancellationToken cancellationToken) => plans.ListAsync(includeRetired: true, cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => (await plans.GetAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost]
    public async Task<IActionResult> Create(SavePlanRequest request, CancellationToken cancellationToken)
    {
        var result = await plans.CreateAsync(request, cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Data }, new { id = result.Data }) : result.ToActionResult(this);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, SavePlanRequest request, CancellationToken cancellationToken) =>
        (await plans.UpdateAsync(id, request, cancellationToken)).ToActionResult(this);

    /// <summary>The features and limits a plan can set, each with its module, suggested tier and the version it arrived in.</summary>
    [HttpGet("entitlements")]
    public IReadOnlyList<EntitlementDefinition> Entitlements() => EntitlementCatalog.All;

    /// <summary>The modules (parts of the product) features and limits are grouped in.</summary>
    [HttpGet("modules")]
    public IReadOnlyList<PlanModule> Modules() => EntitlementCatalog.Modules;
}

[ApiController]
[Route("api/subscription")]
public sealed class SubscriptionApiController(ISubscriptionService subscriptions, ITenantEntitlements entitlements) : ControllerBase
{
    /// <summary>The current tenant's plan, status and what it allows; any signed-in user may ask.</summary>
    [HttpGet("access")]
    public Task<TenantAccess> Access(CancellationToken cancellationToken) => entitlements.GetAsync(cancellationToken);

    [HttpGet]
    [HasPermission(PermissionCodes.Subscription.View)]
    public Task<MySubscription> Get(CancellationToken cancellationToken) => subscriptions.GetMineAsync(cancellationToken);
}
