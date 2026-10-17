using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Domain.Subscriptions;
using Base.Domain.Tenancy;

namespace Base.Application.Subscriptions;

/// <summary>
/// Operates any tenant's subscription (platform: <c>Platform.Tenants.Manage</c>) and shows a tenant its own
/// (<c>Subscription.View</c>). Payments come in through <see cref="RecordPaymentAsync"/>, from an operator or a billing
/// integration; nothing here talks to a payment provider.
/// </summary>
public interface ISubscriptionService
{
    Task<MySubscription> GetMineAsync(CancellationToken cancellationToken = default);

    Task<SubscriptionView?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageView>> UsageAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<ValidationResponse> StartAsync(Guid tenantId, StartSubscriptionRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ChangePlanAsync(Guid tenantId, ChangePlanRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> RecordPaymentAsync(Guid tenantId, string? externalReference, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ExtendTrialAsync(Guid tenantId, int days, CancellationToken cancellationToken = default);

    Task<ValidationResponse> SuspendAsync(Guid tenantId, string reason, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ReinstateAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<ValidationResponse> CancelAsync(Guid tenantId, bool atPeriodEnd, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ResumeAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

internal sealed class SubscriptionService(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ITenantEntitlements entitlements,
    ITenantUsageReader usage,
    IPlanAdministration plans,
    IPlatformTenant platform,
    TimeProvider time) : ISubscriptionService
{
    public async Task<MySubscription> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var offered = await plans.ListAsync(includeRetired: false, cancellationToken);
        if (!tenant.IsResolved)
        {
            return new MySubscription(null, [], offered);
        }

        return new MySubscription(
            await GetAsync(tenant.TenantId, cancellationToken),
            await UsageAsync(tenant.TenantId, cancellationToken),
            offered);
    }

    public async Task<SubscriptionView?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        TenantSubscription? subscription;
        using (tenant.BeginSystemScope())
        {
            subscription = await unitOfWork.Repository<TenantSubscription>().GetAsync(s => s.TenantId == tenantId, asNoTracking: true, cancellationToken: cancellationToken);
        }

        if (subscription is null)
        {
            return null;
        }

        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(subscription.PlanId, cancellationToken);
        return ToView(subscription, plan);
    }

    public async Task<IReadOnlyList<UsageView>> UsageAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var subscription = await GetAsync(tenantId, cancellationToken);
        var plan = subscription is null ? null : (await plans.GetAsync(subscription.PlanId, cancellationToken)).Data;
        var measured = await usage.MeasureAsync(tenantId, cancellationToken);
        return EntitlementCatalog.Limits.Select(definition =>
        {
            var used = measured.GetValueOrDefault(definition.Code);
            return new UsageView(
                definition.Code,
                definition.Name,
                (used + definition.UnitSize - 1) / definition.UnitSize,
                plan?.ValueOf(definition.Code),
                definition.Unit);
        }).ToList();
    }

    public async Task<ValidationResponse> StartAsync(Guid tenantId, StartSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await platform.GetIdAsync(cancellationToken) == tenantId)
        {
            return ValidationResponse.Failure("The platform tenant doesn't subscribe to a plan.");
        }

        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(request.PlanId, cancellationToken);
        if (plan is not { IsActive: true })
        {
            return ValidationResponse.Failure("Choose a plan that is on offer.", "plan.unavailable");
        }

        using (tenant.BeginSystemScope())
        {
            var repository = unitOfWork.Repository<TenantSubscription>();
            if (!await unitOfWork.Repository<Tenant>().ExistsAsync(t => t.Id == tenantId, cancellationToken))
            {
                return ValidationResponse.NotFound("Tenant not found.");
            }

            if (await repository.ExistsAsync(s => s.TenantId == tenantId, cancellationToken))
            {
                return ValidationResponse.Conflict("This tenant already has a subscription; change its plan instead.");
            }

            await repository.AddAsync(TenantSubscription.Start(tenantId, plan, request.BillingCycle, time.GetUtcNow(), request.WithTrial), cancellationToken);
            await unitOfWork.SaveAsync(cancellationToken);
        }

        await entitlements.InvalidateAsync(cancellationToken);
        return ValidationResponse.Success($"Subscribed to {plan.Name}.");
    }

    public async Task<ValidationResponse> ChangePlanAsync(Guid tenantId, ChangePlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(request.PlanId, cancellationToken);
        if (plan is null)
        {
            return ValidationResponse.NotFound("Plan not found.");
        }

        return await ChangeAsync(tenantId, s =>
        {
            if (!plan.IsActive && plan.Id != s.PlanId)
            {
                return ValidationResponse.Failure("That plan is no longer offered.", "plan.unavailable");
            }

            s.ChangePlan(plan, request.BillingCycle);
            return ValidationResponse.Success($"Moved to {plan.Name}.");
        }, cancellationToken);
    }

    public Task<ValidationResponse> RecordPaymentAsync(Guid tenantId, string? externalReference, CancellationToken cancellationToken = default) =>
        ChangeAsync(tenantId, s =>
        {
            if (externalReference?.Length > TenantSubscription.ExternalReferenceMaxLength)
            {
                return ValidationResponse.Failure($"The reference can be at most {TenantSubscription.ExternalReferenceMaxLength} characters.");
            }

            s.RecordPayment(time.GetUtcNow(), externalReference);
            return ValidationResponse.Success("Payment recorded; the subscription is active for another period.");
        }, cancellationToken);

    public Task<ValidationResponse> ExtendTrialAsync(Guid tenantId, int days, CancellationToken cancellationToken = default) =>
        ChangeAsync(tenantId, s =>
        {
            if (s.Status != SubscriptionStatus.Trialing)
            {
                return ValidationResponse.Failure("Only a trial can be extended.");
            }

            if (days is < 1 or > Plan.MaxTrialDays)
            {
                return ValidationResponse.Failure($"Extend by 1 to {Plan.MaxTrialDays} days.");
            }

            s.ExtendTrial(days, time.GetUtcNow());
            return ValidationResponse.Success(string.Create(CultureInfo.InvariantCulture, $"Trial extended by {days} day(s)."));
        }, cancellationToken);

    public Task<ValidationResponse> SuspendAsync(Guid tenantId, string reason, CancellationToken cancellationToken = default) =>
        ChangeAsync(tenantId, s =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return ValidationResponse.Failure(new ValidationError("reason", "Say why, so the tenant and your team know.", "required"));
            }

            if (s.IsEnded || s.Status == SubscriptionStatus.Suspended)
            {
                return ValidationResponse.Failure("This subscription can't be suspended now.");
            }

            s.Suspend(time.GetUtcNow(), reason.Trim());
            return ValidationResponse.Success("Suspended. Its users can sign in only to see why.");
        }, cancellationToken);

    public Task<ValidationResponse> ReinstateAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        ChangeAsync(tenantId, s =>
        {
            if (s.Status != SubscriptionStatus.Suspended)
            {
                return ValidationResponse.Failure("Only a suspended subscription can be reinstated.");
            }

            s.Reinstate(time.GetUtcNow());
            return ValidationResponse.Success($"Reinstated as {SubscriptionStatusText.Describe(s.Status).ToLowerInvariant()}.");
        }, cancellationToken);

    public Task<ValidationResponse> CancelAsync(Guid tenantId, bool atPeriodEnd, CancellationToken cancellationToken = default) =>
        ChangeAsync(tenantId, s =>
        {
            if (s.IsEnded)
            {
                return ValidationResponse.Failure("This subscription has already ended.");
            }

            s.Cancel(time.GetUtcNow(), atPeriodEnd);
            return ValidationResponse.Success(s.IsEnded ? "Cancelled." : "It will end when the current period is over.");
        }, cancellationToken);

    public Task<ValidationResponse> ResumeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        ChangeAsync(tenantId, s =>
        {
            if (!s.CancelAtPeriodEnd || s.IsEnded)
            {
                return ValidationResponse.Failure("This subscription isn't set to cancel.");
            }

            s.Resume(time.GetUtcNow());
            return ValidationResponse.Success("It will renew as usual.");
        }, cancellationToken);

    internal static SubscriptionView ToView(TenantSubscription s, Plan? plan) => new(
        s.Id,
        s.TenantId,
        s.PlanId,
        plan?.Name ?? "(removed plan)",
        s.Status,
        s.BillingCycle,
        plan?.PriceFor(s.BillingCycle) ?? 0,
        plan?.Currency ?? string.Empty,
        s.StartedAt,
        s.TrialEndsAt,
        s.CurrentPeriodEnd,
        s.CancelAtPeriodEnd,
        s.StatusChangedAt,
        s.StatusReason,
        s.ExternalReference);

    private async Task<ValidationResponse> ChangeAsync(Guid tenantId, Func<TenantSubscription, ValidationResponse> change, CancellationToken cancellationToken)
    {
        ValidationResponse result;
        using (tenant.BeginSystemScope())
        {
            var subscription = await unitOfWork.Repository<TenantSubscription>().GetAsync(s => s.TenantId == tenantId, cancellationToken: cancellationToken);
            if (subscription is null)
            {
                return ValidationResponse.NotFound("This tenant has no subscription.");
            }

            result = change(subscription);
            if (result.IsError)
            {
                return result;
            }

            await unitOfWork.SaveAsync(cancellationToken);
        }

        await entitlements.InvalidateAsync(cancellationToken);
        return result;
    }
}

/// <summary>Usage of every limit for any tenant, in measured units (accounts, bytes). Implemented in Infrastructure.</summary>
public interface ITenantUsageReader
{
    Task<IReadOnlyDictionary<string, long>> MeasureAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
