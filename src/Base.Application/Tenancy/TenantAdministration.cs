using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Subscriptions;
using Base.Domain.Subscriptions;

namespace Base.Application.Tenancy;

/// <summary>
/// The organisations using the platform: creating and provisioning them, their domains, and switching them off.
/// Platform work (<c>Platform.Tenants.View</c> / <c>Manage</c>). Subscriptions are changed through <see cref="ISubscriptionService"/>.
/// </summary>
public interface ITenantAdministration
{
    Task<PagedResult<TenantSummary>> SearchAsync(TenantQuery query, CancellationToken cancellationToken = default);

    Task<ValidationResponse<TenantDetails>> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the tenant with its domain and subscription, then invites its first administrator (who sets their own
    /// password from the email). Links in that email use the tenant's domain.
    /// </summary>
    Task<ValidationResponse<Guid>> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> UpdateAsync(Guid tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> AddDomainAsync(Guid tenantId, string host, CancellationToken cancellationToken = default);

    /// <summary>Only verified domains resolve to the tenant. Verify once the tenant has pointed DNS at the platform.</summary>
    Task<ValidationResponse> VerifyDomainAsync(Guid tenantId, Guid domainId, CancellationToken cancellationToken = default);

    Task<ValidationResponse> RemoveDomainAsync(Guid tenantId, Guid domainId, CancellationToken cancellationToken = default);

    /// <summary>A deactivated tenant gets 403 on every request, whatever its subscription. The platform tenant can't be.</summary>
    Task<ValidationResponse> SetActiveAsync(Guid tenantId, bool active, CancellationToken cancellationToken = default);
}

public sealed record TenantQuery(string? Search = null, SubscriptionStatus? Status = null, int Page = 1, int PageSize = 25);

public sealed record TenantSummary(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    bool IsPlatform,
    string? ContactEmail,
    string? PrimaryHost,
    string? PlanName,
    SubscriptionStatus? Status,
    DateTimeOffset? PeriodEnd,
    int UserCount,
    DateTimeOffset DateCreated);

public sealed record TenantDomainView(Guid Id, string Host, bool IsVerified);

public sealed record TenantDetails(
    TenantSummary Tenant,
    IReadOnlyList<TenantDomainView> Domains,
    SubscriptionView? Subscription,
    IReadOnlyList<UsageView> Usage);

/// <param name="Host">Optional host name the tenant is reached at, e.g. acme.example.com (added as verified).</param>
/// <param name="PlanId">Null leaves the tenant without a subscription: unrestricted, like the platform tenant.</param>
public sealed record CreateTenantRequest(
    string Name,
    string Code,
    string? Host,
    string AdminEmail,
    string AdminName,
    Guid? PlanId,
    BillingCycle BillingCycle = BillingCycle.Monthly,
    bool WithTrial = true);

public sealed record UpdateTenantRequest(string Name, string? ContactEmail);
