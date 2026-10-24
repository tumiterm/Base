using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Payments;
using Base.Application.Security;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Base.Domain.Tenancy;

namespace Base.Application.Subscriptions;

/// <summary>
/// Operates any tenant's subscription (platform: <c>Platform.Tenants.Manage</c>) and shows a tenant its own
/// (<c>Subscription.View</c>). Payments come in through <see cref="RecordPaymentAsync"/>, from an operator or a billing
/// integration, or online through <see cref="IPaymentService"/>. Changing plan follows <see cref="PlanChangeRules"/>: an
/// upgrade applies now, a downgrade or change of cycle when the paid period ends, and never to a plan whose limits the
/// organisation is over (the operator may override that).
/// </summary>
public interface ISubscriptionService
{
    Task<MySubscription> GetMineAsync(CancellationToken cancellationToken = default);

    Task<SubscriptionView?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageView>> UsageAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<ValidationResponse> StartAsync(Guid tenantId, StartSubscriptionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The operator's change: now or at the end of the period, with a reason; the organisation is told.</summary>
    Task<ValidationResponse> ChangePlanAsync(Guid tenantId, ChangePlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>What moving any tenant to a plan and cycle would do.</summary>
    Task<ValidationResponse<PlanChangePreview>> PreviewChangeAsync(Guid tenantId, Guid planId, BillingCycle cycle, CancellationToken cancellationToken = default);

    /// <summary>What moving this organisation to a plan and cycle would do. Needs <c>Subscription.View</c>.</summary>
    Task<ValidationResponse<PlanChangePreview>> PreviewMineAsync(Guid planId, BillingCycle cycle, CancellationToken cancellationToken = default);

    /// <summary>
    /// This organisation's change that needs no payment: a downgrade or change of cycle (when the period ends), or an
    /// upgrade with less to pay than any payment provider takes (now). Needs <c>Subscription.Pay</c>.
    /// </summary>
    Task<ValidationResponse> ChangeMineAsync(Guid planId, BillingCycle cycle, CancellationToken cancellationToken = default);

    /// <summary>Keeps this organisation's current plan: withdraws the scheduled change. Needs <c>Subscription.Pay</c>.</summary>
    Task<ValidationResponse> KeepMyPlanAsync(CancellationToken cancellationToken = default);

    /// <summary>The operator withdraws a tenant's scheduled change.</summary>
    Task<ValidationResponse> CancelScheduledChangeAsync(Guid tenantId, CancellationToken cancellationToken = default);

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
    ICurrentUser currentUser,
    IEnumerable<IPaymentGateway> gateways,
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
        var scheduled = subscription.ScheduledPlanId is { } scheduledId ? await unitOfWork.Repository<Plan>().GetByIdAsync(scheduledId, cancellationToken) : null;
        return ToView(subscription, plan, scheduled);
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
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ValidationResponse.Failure(new ValidationError(nameof(request.Reason), "Say why: the organisation is told, and it goes in the audit trail.", "required"));
        }

        var preview = await PreviewChangeAsync(tenantId, request.PlanId, request.BillingCycle, cancellationToken);
        if (preview.IsError)
        {
            return preview;
        }

        var change = preview.Data!;
        if (change.IsCurrent)
        {
            return ValidationResponse.Failure("That's already the tenant's plan and billing cycle.");
        }

        if (change.IsBlocked && !request.AllowOverLimits)
        {
            return ValidationResponse.Failure(
                $"{change.To.Name} allows less than the tenant uses: {string.Join(" ", change.OverLimits.Select(l => l.Describe()))} Ask them to get under it, or change anyway.",
                "plan.over-limits");
        }

        if (request.When == PlanChangeTiming.AtPeriodEnd && !change.CanWaitForPeriodEnd)
        {
            return ValidationResponse.Failure("There's no running period to wait for (or the subscription ends with it): change it now instead.");
        }

        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(request.PlanId, cancellationToken);
        var reason = request.Reason.Trim();
        return await ChangeAsync(tenantId, s =>
        {
            if (!plan!.IsActive && plan.Id != s.PlanId)
            {
                return ValidationResponse.Failure("That plan is no longer offered.", "plan.unavailable");
            }

            if (request.When == PlanChangeTiming.AtPeriodEnd)
            {
                s.ScheduleChange(plan, request.BillingCycle, time.GetUtcNow(), PlanChangeSource.Operator, reason);
                return ValidationResponse.Success(string.Create(CultureInfo.InvariantCulture, $"Moves to {plan.Name} on {s.ScheduledChangeAt:d MMMM yyyy}. The tenant has been told."));
            }

            s.ChangePlan(plan, request.BillingCycle, PlanChangeSource.Operator, reason);
            return ValidationResponse.Success($"Moved to {plan.Name}. The tenant has been told.");
        }, cancellationToken);
    }

    public async Task<ValidationResponse<PlanChangePreview>> PreviewChangeAsync(Guid tenantId, Guid planId, BillingCycle cycle, CancellationToken cancellationToken = default)
    {
        TenantSubscription? subscription;
        bool renews;
        using (tenant.BeginSystemScope())
        {
            subscription = await unitOfWork.Repository<TenantSubscription>().GetAsync(s => s.TenantId == tenantId, asNoTracking: true, cancellationToken: cancellationToken);
            renews = await unitOfWork.Repository<RecurringPayment>().ExistsAsync(r => r.TenantId == tenantId && r.Status == RecurringPaymentStatus.Active, cancellationToken);
        }

        if (subscription is null)
        {
            return ValidationResponse.NotFound("This organisation has no subscription.").As<PlanChangePreview>();
        }

        var to = await plans.GetAsync(planId, cancellationToken);
        if (to.IsError)
        {
            return ValidationResponse.NotFound("Plan not found.").As<PlanChangePreview>();
        }

        var from = await plans.GetAsync(subscription.PlanId, cancellationToken);
        if (from.IsError)
        {
            return ValidationResponse.Failure("The current plan no longer exists; the operator has to change it.").As<PlanChangePreview>();
        }

        var now = time.GetUtcNow();
        var (kind, effectiveAt, due) = PlanChangeRules.Classify(subscription, from.Data!, to.Data!, cycle, now);
        var (lost, gained) = PlanChangeRules.Features(from.Data!, to.Data!);
        var over = planId == subscription.PlanId ? [] : PlanChangeRules.OverLimits(to.Data!, await usage.MeasureAsync(tenantId, cancellationToken));
        ScheduledChangeView? scheduled = null;
        if (subscription.ScheduledPlanId is { } scheduledId && (await plans.GetAsync(scheduledId, cancellationToken)).Data is { } next)
        {
            var nextCycle = subscription.ScheduledBillingCycle ?? subscription.BillingCycle;
            scheduled = new ScheduledChangeView(next.Id, next.Name, nextCycle, next.PriceFor(nextCycle), next.Currency, subscription.ScheduledChangeAt!.Value);
        }

        return ValidationResponse.Success(new PlanChangePreview(
            tenantId,
            from.Data!,
            subscription.BillingCycle,
            to.Data!,
            cycle,
            kind,
            effectiveAt,
            due,
            subscription.CurrentPeriodEnd,
            subscription.HasAccess && subscription.CurrentPeriodEnd > now && !subscription.CancelAtPeriodEnd,
            over,
            lost,
            gained,
            renews,
            scheduled));
    }

    public async Task<ValidationResponse<PlanChangePreview>> PreviewMineAsync(Guid planId, BillingCycle cycle, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Subscription.View) || !tenant.IsResolved)
        {
            return ValidationResponse.Forbidden().As<PlanChangePreview>();
        }

        var preview = await PreviewChangeAsync(tenant.TenantId, planId, cycle, cancellationToken);
        return preview.IsSuccess && !preview.Data!.To.IsActive && planId != preview.Data.From.Id
            ? ValidationResponse.Failure("Choose one of the plans on offer.", "plan.unavailable").As<PlanChangePreview>()
            : preview;
    }

    public async Task<ValidationResponse> ChangeMineAsync(Guid planId, BillingCycle cycle, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Subscription.Pay))
        {
            return ValidationResponse.Forbidden();
        }

        var preview = await PreviewMineAsync(planId, cycle, cancellationToken);
        if (preview.IsError)
        {
            return preview;
        }

        var change = preview.Data!;
        if (change.IsCurrent)
        {
            return change.Scheduled is null ? ValidationResponse.Failure("That's already your plan.") : await KeepMyPlanAsync(cancellationToken);
        }

        if (change.IsBlocked)
        {
            return ValidationResponse.Failure(
                $"{change.To.Name} allows less than you use: {string.Join(" ", change.OverLimits.Select(l => l.Describe()))}",
                "plan.over-limits");
        }

        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(planId, cancellationToken);
        switch (change.Kind)
        {
            case PlanChangeKind.AtPeriodEnd:
                return await ChangeAsync(tenant.TenantId, s =>
                {
                    if (s.CancelAtPeriodEnd)
                    {
                        return ValidationResponse.Failure("Your subscription is set to end with this period. Keep it renewing first.");
                    }

                    s.ScheduleChange(plan!, cycle, time.GetUtcNow(), PlanChangeSource.Tenant);
                    return ValidationResponse.Success(string.Create(CultureInfo.InvariantCulture, $"You'll move to {plan!.Name} on {s.ScheduledChangeAt:d MMMM yyyy}. Until then nothing changes."));
                }, cancellationToken);

            case PlanChangeKind.Upgrade when change.AmountDueNow < SmallestCharge(change.Currency):
                return await ChangeAsync(tenant.TenantId, s =>
                {
                    s.Upgrade(plan!, PlanChangeSource.Tenant);
                    return ValidationResponse.Success($"You're on {plan!.Name} now; there was nothing to pay for the rest of this period.");
                }, cancellationToken);

            default:
                return ValidationResponse.Failure("This change is paid for: continue to payment.", "plan.payment");
        }
    }

    public Task<ValidationResponse> KeepMyPlanAsync(CancellationToken cancellationToken = default) =>
        !currentUser.HasPermission(PermissionCodes.Subscription.Pay) || !tenant.IsResolved
            ? Task.FromResult(ValidationResponse.Forbidden())
            : WithdrawAsync(tenant.TenantId, PlanChangeSource.Tenant, cancellationToken);

    public Task<ValidationResponse> CancelScheduledChangeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        WithdrawAsync(tenantId, PlanChangeSource.Operator, cancellationToken);

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

    internal static SubscriptionView ToView(TenantSubscription s, Plan? plan, Plan? scheduled = null) => new(
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
        s.ExternalReference,
        scheduled is null || s.ScheduledChangeAt is not { } at
            ? null
            : new ScheduledChangeView(scheduled.Id, scheduled.Name, s.ScheduledBillingCycle ?? s.BillingCycle, scheduled.PriceFor(s.ScheduledBillingCycle ?? s.BillingCycle), scheduled.Currency, at));

    /// <summary>Less than this can't be paid online in <paramref name="currency"/>: no payment provider takes it.</summary>
    private decimal SmallestCharge(string currency)
    {
        var minimums = gateways.Where(g => g.Currencies.Contains(currency, StringComparer.OrdinalIgnoreCase)).Select(g => g.MinimumCharge).ToList();
        return minimums.Count == 0 ? 0m : minimums.Min();
    }

    private Task<ValidationResponse> WithdrawAsync(Guid tenantId, PlanChangeSource source, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, s =>
        {
            if (!s.HasScheduledChange)
            {
                return ValidationResponse.Failure("No change of plan is waiting.");
            }

            s.CancelScheduledChange(source, "Kept the current plan");
            return ValidationResponse.Success("The plan stays as it is.");
        }, cancellationToken);

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
