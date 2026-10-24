using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Base.Domain.Tenancy;
using Microsoft.Extensions.Logging;

namespace Base.Application.Payments;

/// <summary>Stops automatic renewals, at their gateway first so nothing more is charged. The caller saves.</summary>
internal sealed partial class RecurringPayments(IEnumerable<IPaymentGateway> gateways, TimeProvider time, ILogger<RecurringPayments> logger)
{
    public async Task<GatewayResult> StopAsync(RecurringPayment recurring, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recurring);
        if (!recurring.IsLive)
        {
            return GatewayResult.Ok();
        }

        if (recurring.Token is { } token)
        {
            var gateway = gateways.FirstOrDefault(g => string.Equals(g.Code, recurring.Gateway, StringComparison.OrdinalIgnoreCase));
            var stopped = gateway is null
                ? GatewayResult.Failed($"{recurring.Gateway} isn't configured on this server, so it can't be told to stop.")
                : await gateway.CancelRecurringAsync(token, cancellationToken);
            if (!stopped.Succeeded)
            {
                var error = $"{gateway?.Name ?? recurring.Gateway} couldn't be told to stop charging: {stopped.Error} Try again, or cancel it in the {gateway?.Name ?? recurring.Gateway} dashboard.";
                recurring.StopFailed(error);
                LogStopFailed(logger, recurring.Id, recurring.Gateway, stopped.Error);
                return GatewayResult.Failed(error);
            }
        }

        recurring.Cancel(reason, time.GetUtcNow());
        return GatewayResult.Ok();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic renewal {RecurringPaymentId} could not be stopped at {Gateway}: {Error}")]
    private static partial void LogStopFailed(ILogger logger, Guid recurringPaymentId, string gateway, string? error);
}

/// <summary>
/// Keeps at most one automatic renewal per organisation, for the plan it pays for: a new one replaces the old, a
/// payment for another plan or cycle stops it, and so does the subscription ending. Tells the organisation when the
/// gateway stopped one by itself.
/// </summary>
internal sealed class RecurringPaymentLifecycle(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    RecurringPayments recurringPayments,
    INotificationService notifications,
    IEnumerable<IPaymentGateway> gateways)
    : IDomainEventHandler<RecurringPaymentActivated>,
      IDomainEventHandler<PaymentSucceeded>,
      IDomainEventHandler<SubscriptionStatusChanged>,
      IDomainEventHandler<RecurringPaymentStoppedByGateway>
{
    public async Task HandleAsync(RecurringPaymentActivated domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await StopLiveAsync(domainEvent.TenantId, r => r.Id != domainEvent.RecurringPaymentId, "Replaced by a newer automatic renewal.", cancellationToken);
    }

    public async Task HandleAsync(PaymentSucceeded domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var payment = await unitOfWork.Repository<Payment>().GetByIdAsync(domainEvent.PaymentId, cancellationToken);
        if (payment is null || payment.RecurringPaymentId is not null || payment.IsUpgrade)
        {
            // An upgrade's difference doesn't replace the renewal: the renewal follows the new plan (RenewalFollowsPlan).
            return;
        }

        // A one-off payment for the same plan and cycle pays ahead; for another one it replaces what renews.
        await StopLiveAsync(domainEvent.TenantId, r => r.PlanId != payment.PlanId || r.BillingCycle != payment.BillingCycle, "Replaced by a payment for another plan or period.", cancellationToken);
    }

    public async Task HandleAsync(SubscriptionStatusChanged domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.To is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired)
        {
            await StopLiveAsync(domainEvent.TenantId, _ => true, "The subscription ended.", cancellationToken);
        }
    }

    public async Task HandleAsync(RecurringPaymentStoppedByGateway domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await TellStoppedAsync(domainEvent.TenantId, domainEvent.RecurringPaymentId, null, cancellationToken);
    }

    /// <summary>Emails the organisation's contact that its automatic renewal stopped, and why.</summary>
    internal async Task TellStoppedAsync(Guid tenantId, Guid recurringPaymentId, string? reason, CancellationToken cancellationToken)
    {
        RecurringPayment? recurring;
        Tenant? owner;
        Plan? plan;
        using (tenant.BeginSystemScope())
        {
            recurring = await unitOfWork.Repository<RecurringPayment>().GetByIdAsync(recurringPaymentId, cancellationToken);
            owner = await unitOfWork.Repository<Tenant>().GetByIdAsync(tenantId, cancellationToken);
            plan = recurring is null ? null : await unitOfWork.Repository<Plan>().GetByIdAsync(recurring.PlanId, cancellationToken);
        }

        if (recurring is null || owner?.ContactEmail is not { } email)
        {
            return;
        }

        var gateway = gateways.FirstOrDefault(g => string.Equals(g.Code, recurring.Gateway, StringComparison.OrdinalIgnoreCase))?.Name ?? recurring.Gateway;
        await notifications.SendAsync(
            new Notification(
                NotificationTemplates.Subscriptions.AutoRenewalStopped,
                NotificationRecipient.ToEmail(email, owner.Name),
                new
                {
                    PlanName = plan?.Name ?? "current",
                    Gateway = gateway,
                    Amount = recurring.Currency + " " + recurring.Amount.ToString("N2", CultureInfo.InvariantCulture),
                    Reason = reason ?? $"{gateway} stopped it",
                }),
            cancellationToken);
    }

    private async Task StopLiveAsync(Guid tenantId, Func<RecurringPayment, bool> which, string reason, CancellationToken cancellationToken)
    {
        IReadOnlyList<RecurringPayment> live;
        using (tenant.BeginSystemScope())
        {
            live = await unitOfWork.Repository<RecurringPayment>().GetAllAsync(
                filter: r => r.TenantId == tenantId && (r.Status == RecurringPaymentStatus.Active || r.Status == RecurringPaymentStatus.Pending),
                cancellationToken: cancellationToken);
            var stopping = live.Where(which).ToList();
            if (stopping.Count == 0)
            {
                return;
            }

            foreach (var recurring in stopping)
            {
                await recurringPayments.StopAsync(recurring, reason, cancellationToken);
            }

            await unitOfWork.SaveAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Keeps an automatic renewal charging for the plan and cycle of the next period: after an upgrade or the operator's
/// change, and when a change is scheduled for the end of the period (or withdrawn). The gateway is asked to change the
/// amount and frequency; if it can't, the renewal is stopped there and the organisation told, so it is never charged
/// the wrong amount. A payment for another plan already replaced the renewal (<see cref="RecurringPaymentLifecycle"/>).
/// </summary>
internal sealed partial class RenewalFollowsPlan(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    RecurringPayments recurringPayments,
    RecurringPaymentLifecycle lifecycle,
    IEnumerable<IPaymentGateway> gateways,
    ILogger<RenewalFollowsPlan> logger)
    : IDomainEventHandler<SubscriptionPlanChanged>, IDomainEventHandler<SubscriptionChangeScheduled>
{
    public async Task HandleAsync(SubscriptionPlanChanged domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Source != PlanChangeSource.Payment)
        {
            await FollowAsync(domainEvent.TenantId, cancellationToken);
        }
    }

    public async Task HandleAsync(SubscriptionChangeScheduled domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Source != PlanChangeSource.Payment)
        {
            await FollowAsync(domainEvent.TenantId, cancellationToken);
        }
    }

    private async Task FollowAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        RecurringPayment? stopped = null;
        string? why = null;
        using (tenant.BeginSystemScope())
        {
            var recurring = await unitOfWork.Repository<RecurringPayment>().GetAsync(
                r => r.TenantId == tenantId && r.Status == RecurringPaymentStatus.Active && r.Token != null, cancellationToken: cancellationToken);
            var subscription = recurring is null ? null : await unitOfWork.Repository<TenantSubscription>().GetAsync(s => s.TenantId == tenantId, asNoTracking: true, cancellationToken: cancellationToken);
            if (recurring is null || subscription is null)
            {
                return;
            }

            var planId = subscription.ScheduledPlanId ?? subscription.PlanId;
            var cycle = subscription.ScheduledBillingCycle ?? subscription.BillingCycle;
            var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(planId, cancellationToken);
            var amount = plan?.PriceFor(cycle) ?? 0m;
            if (plan is not null && recurring.Matches(plan.Id, cycle, amount))
            {
                return;
            }

            var gateway = gateways.FirstOrDefault(g => string.Equals(g.Code, recurring.Gateway, StringComparison.OrdinalIgnoreCase));
            GatewayResult updated;
            if (plan is null || amount <= 0 || !string.Equals(plan.Currency, recurring.Currency, StringComparison.OrdinalIgnoreCase))
            {
                updated = GatewayResult.Failed("the new plan has nothing to charge in the same currency");
            }
            else if (gateway?.MaximumRecurringCharge is { } maximum && amount > maximum)
            {
                updated = GatewayResult.Failed($"{gateway.Name} renews automatically only up to {plan.Currency} {maximum.ToString("N2", CultureInfo.InvariantCulture)}");
            }
            else if (gateway is not { SupportsRecurringUpdate: true })
            {
                updated = GatewayResult.Failed($"{gateway?.Name ?? recurring.Gateway} can't change what it charges");
            }
            else
            {
                updated = await gateway.UpdateRecurringAsync(recurring.Token!, amount, cycle, cancellationToken);
            }

            if (updated.Succeeded)
            {
                recurring.Follow(plan!.Id, cycle, amount);
                LogFollowed(logger, recurring.Id, plan.Name, amount);
            }
            else
            {
                why = $"your plan changed to {plan?.Name ?? "another plan"} and {updated.Error?.TrimEnd('.')}";
                LogCouldNotFollow(logger, recurring.Id, updated.Error);
                var stop = await recurringPayments.StopAsync(recurring, "The plan changed and the renewal couldn't follow it.", cancellationToken);
                stopped = stop.Succeeded ? recurring : null;
            }

            await unitOfWork.SaveAsync(cancellationToken);
        }

        if (stopped is not null)
        {
            await lifecycle.TellStoppedAsync(tenantId, stopped.Id, why, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic renewal {RecurringPaymentId} now charges {Amount} for {Plan}")]
    private static partial void LogFollowed(ILogger logger, Guid recurringPaymentId, string plan, decimal amount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic renewal {RecurringPaymentId} couldn't follow the plan change ({Error}); stopping it")]
    private static partial void LogCouldNotFollow(ILogger logger, Guid recurringPaymentId, string? error);
}
