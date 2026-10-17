using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Operations;
using Base.Application.Settings;
using Base.Domain.Subscriptions;
using Base.Domain.Tenancy;

namespace Base.Application.Subscriptions;

/// <summary>
/// Moves subscriptions on as time passes: trial reminders, ended trials and unpaid renewals to past due, past due
/// beyond the grace period to suspended, and cancel-at-period-end to cancelled. Runs hourly in system scope.
/// </summary>
internal sealed class SubscriptionLifecycleJob(
    IUnitOfWork unitOfWork,
    IApplicationSettingService settings,
    ITenantEntitlements entitlements,
    TimeProvider time) : IScheduledJob
{
    public string Code => "subscription-lifecycle";

    public string Name => "Subscription lifecycle";

    public string Description => "Sends trial reminders, marks unpaid subscriptions past due and suspends them after the grace period.";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var grace = TimeSpan.FromDays(await settings.GetIntAsync(SettingCodes.Subscriptions.GraceDays, cancellationToken));
        var notice = TimeSpan.FromDays(await settings.GetIntAsync(SettingCodes.Subscriptions.TrialReminderDays, cancellationToken));
        var horizon = now + notice;

        var due = await unitOfWork.Repository<TenantSubscription>().GetAllAsync(
            s => (s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue)
                && (s.CurrentPeriodEnd <= horizon || s.Status == SubscriptionStatus.PastDue),
            includeProperties: null,
            cancellationToken: cancellationToken);

        int changed = 0, reminded = 0;
        foreach (var subscription in due)
        {
            if (subscription.TrialEndsSoon(now, notice))
            {
                subscription.MarkTrialReminderSent(now);
                reminded++;
            }

            // A trial that ended long ago can pass through past due to suspended in one run.
            var steps = 0;
            while (steps < 3 && subscription.Advance(now, grace))
            {
                steps++;
            }

            changed += steps > 0 ? 1 : 0;
        }

        await unitOfWork.SaveAsync(cancellationToken);
        if (changed > 0)
        {
            await entitlements.InvalidateAsync(cancellationToken);
        }

        return string.Create(CultureInfo.InvariantCulture, $"Checked {due.Count}; {changed} changed status, {reminded} trial reminder(s)");
    }
}

/// <summary>Emails the tenant's contact about its trial ending and about status changes that need action.</summary>
internal sealed class SubscriptionNotices(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    INotificationService notifications)
    : IDomainEventHandler<SubscriptionStatusChanged>, IDomainEventHandler<SubscriptionTrialEnding>
{
    public async Task HandleAsync(SubscriptionTrialEnding domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await SendAsync(domainEvent.TenantId, domainEvent.PlanId, NotificationTemplates.Subscriptions.TrialEnding, plan => new
        {
            PlanName = plan,
            EndsOn = domainEvent.EndsAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
        }, cancellationToken);
    }

    public async Task HandleAsync(SubscriptionStatusChanged domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var worthTelling = domainEvent.To is SubscriptionStatus.PastDue or SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled or SubscriptionStatus.Expired
            || (domainEvent.To == SubscriptionStatus.Active && domainEvent.From is SubscriptionStatus.PastDue or SubscriptionStatus.Suspended);
        if (!worthTelling)
        {
            return;
        }

        Guid planId;
        using (tenant.BeginSystemScope())
        {
            planId = await unitOfWork.Repository<TenantSubscription>().GetAsync(s => s.Id == domainEvent.SubscriptionId, s => s.PlanId, cancellationToken);
        }

        await SendAsync(domainEvent.TenantId, planId, NotificationTemplates.Subscriptions.StatusChanged, plan => new
        {
            PlanName = plan,
            Status = SubscriptionStatusText.Describe(domainEvent.To).ToLowerInvariant(),
            Reason = domainEvent.Reason ?? "no reason given",
        }, cancellationToken);
    }

    private async Task SendAsync(Guid tenantId, Guid planId, string template, Func<string, object> data, CancellationToken cancellationToken)
    {
        // Raised by the lifecycle job in system scope: the message belongs to the subscription's tenant.
        tenant.SetTenant(tenantId, null);
        var owner = await unitOfWork.Repository<Tenant>().GetByIdAsync(tenantId, cancellationToken);
        if (owner?.ContactEmail is not { } email)
        {
            return;
        }

        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(planId, cancellationToken);
        await notifications.SendAsync(new Notification(template, NotificationRecipient.ToEmail(email, owner.Name), data(plan?.Name ?? "current")), cancellationToken);
    }
}
