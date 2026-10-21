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
        if (payment is null || payment.RecurringPaymentId is not null)
        {
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
        RecurringPayment? recurring;
        Tenant? owner;
        Plan? plan;
        using (tenant.BeginSystemScope())
        {
            recurring = await unitOfWork.Repository<RecurringPayment>().GetByIdAsync(domainEvent.RecurringPaymentId, cancellationToken);
            owner = await unitOfWork.Repository<Tenant>().GetByIdAsync(domainEvent.TenantId, cancellationToken);
            plan = recurring is null ? null : await unitOfWork.Repository<Plan>().GetByIdAsync(recurring.PlanId, cancellationToken);
        }

        if (recurring is null || owner?.ContactEmail is not { } email)
        {
            return;
        }

        await notifications.SendAsync(
            new Notification(
                NotificationTemplates.Subscriptions.AutoRenewalStopped,
                NotificationRecipient.ToEmail(email, owner.Name),
                new
                {
                    PlanName = plan?.Name ?? "current",
                    Gateway = gateways.FirstOrDefault(g => string.Equals(g.Code, recurring.Gateway, StringComparison.OrdinalIgnoreCase))?.Name ?? recurring.Gateway,
                    Amount = recurring.Currency + " " + recurring.Amount.ToString("N2", CultureInfo.InvariantCulture),
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
