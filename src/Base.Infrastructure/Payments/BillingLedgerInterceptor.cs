using System.Diagnostics;
using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Base.Infrastructure.Payments;

/// <summary>
/// Writes the billing history (<see cref="BillingEvent"/>) from what is being saved: payments, automatic renewals and
/// subscriptions, and the subscription events that carry a reason. Runs before the outbox interceptor (which takes the
/// domain events away), so the history commits or rolls back with the change it describes. Keeps it append-only.
/// </summary>
internal sealed class BillingLedgerInterceptor(ICurrentUser currentUser, TimeProvider time) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Record(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Record(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var tracked = context.ChangeTracker.Entries().ToList();
        if (tracked.Any(e => e.Entity is BillingEvent && e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("The billing history is append-only; its entries cannot be changed or deleted.");
        }

        var lines = new List<BillingEvent>();
        foreach (var entry in tracked.Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            switch (entry.Entity)
            {
                case Payment payment:
                    lines.AddRange(FromPayment(entry, payment));
                    break;
                case RecurringPayment recurring:
                    lines.AddRange(FromRenewal(entry, recurring));
                    break;
                case TenantSubscription subscription:
                    lines.AddRange(FromSubscription(entry, subscription));
                    break;
            }
        }

        context.AddRange(lines);
    }

    private IEnumerable<BillingEvent> FromPayment(EntityEntry entry, Payment p)
    {
        if (entry.State == EntityState.Added)
        {
            yield return p.IsRenewal
                ? Line(p.TenantId, BillingEventKind.RenewalCharged, p.Gateway).ForPlan(p.PlanId, p.BillingCycle).ForPayment(p.Amount, p.Currency, p.Reference, p.Gateway).With(p.Description)
                : Line(p.TenantId, BillingEventKind.CheckoutStarted).ForPlan(p.PlanId, p.BillingCycle).ForPayment(p.Amount, p.Currency, p.Reference, p.Gateway)
                    .With(p.Description + (p.IsUpgrade ? " (the difference for an upgrade)" : p.RecurringPaymentId is not null ? " (sets up automatic renewal)" : string.Empty));
            yield break;
        }

        if (!Changed(entry, nameof(Payment.Status)))
        {
            yield break;
        }

        var kind = p.Status switch
        {
            PaymentStatus.Succeeded => BillingEventKind.PaymentReceived,
            PaymentStatus.Failed => BillingEventKind.PaymentFailed,
            PaymentStatus.Cancelled => BillingEventKind.PaymentCancelled,
            PaymentStatus.Expired => BillingEventKind.PaymentExpired,
            _ => (BillingEventKind?)null,
        };
        if (kind is { } k)
        {
            // Settled by the provider's word when nobody is signed in (its notification, or its answer when asked).
            yield return Line(p.TenantId, k, p.Gateway).ForPlan(p.PlanId, p.BillingCycle).ForPayment(p.Amount, p.Currency, p.Reference, p.Gateway)
                .With(p.Description, p.Status == PaymentStatus.Succeeded ? null : p.FailureReason);
        }
    }

    private IEnumerable<BillingEvent> FromRenewal(EntityEntry entry, RecurringPayment r)
    {
        if (entry.State == EntityState.Added)
        {
            yield break; // its first payment's checkout is recorded
        }

        if (Changed(entry, nameof(RecurringPayment.Status)))
        {
            if (r.Status == RecurringPaymentStatus.Active)
            {
                yield return Line(r.TenantId, BillingEventKind.RenewalOn, r.Gateway).ForPlan(r.PlanId, r.BillingCycle).ForPayment(r.Amount, r.Currency, null, r.Gateway);
            }
            else if (r.Status == RecurringPaymentStatus.Cancelled)
            {
                yield return Line(r.TenantId, BillingEventKind.RenewalStopped).ForPlan(r.PlanId, r.BillingCycle).ForPayment(r.Amount, r.Currency, null, r.Gateway).With(reason: r.CancelReason);
            }

            yield break;
        }

        if (Changed(entry, nameof(RecurringPayment.StopError)) && r.StopError is not null)
        {
            yield return Line(r.TenantId, BillingEventKind.RenewalStopFailed).ForPlan(r.PlanId, r.BillingCycle).ForPayment(r.Amount, r.Currency, null, r.Gateway).With(reason: r.StopError);
        }

        if (Changed(entry, nameof(RecurringPayment.Amount)) || Changed(entry, nameof(RecurringPayment.PlanId)) || Changed(entry, nameof(RecurringPayment.BillingCycle)))
        {
            yield return Line(r.TenantId, BillingEventKind.RenewalChanged).ForPlan(r.PlanId, r.BillingCycle, (Guid?)entry.Property(nameof(RecurringPayment.PlanId)).OriginalValue)
                .ForPayment(r.Amount, r.Currency, null, r.Gateway);
        }
    }

    private IEnumerable<BillingEvent> FromSubscription(EntityEntry entry, TenantSubscription s)
    {
        if (entry.State == EntityState.Added)
        {
            yield return Line(s.TenantId, BillingEventKind.Subscribed).ForPlan(s.PlanId, s.BillingCycle)
                .With(string.Create(CultureInfo.InvariantCulture, $"{SubscriptionStatusLabel(s.Status)} until {s.CurrentPeriodEnd:yyyy-MM-dd}"));
        }

        foreach (var raised in s.DomainEvents)
        {
            switch (raised)
            {
                case SubscriptionStatusChanged changed:
                    yield return Line(s.TenantId, BillingEventKind.StatusChanged).ForPlan(s.PlanId, s.BillingCycle)
                        .With($"{SubscriptionStatusLabel(changed.From)} → {SubscriptionStatusLabel(changed.To)}", changed.Reason);
                    break;
                case SubscriptionPlanChanged changed:
                    yield return Line(s.TenantId, BillingEventKind.PlanChanged).ForPlan(changed.ToPlanId, changed.ToCycle, changed.FromPlanId)
                        .With(Describe(changed.Source), changed.Reason);
                    break;
                case SubscriptionChangeScheduled { ToPlanId: { } to } scheduled:
                    yield return Line(s.TenantId, BillingEventKind.ChangeScheduled).ForPlan(to, scheduled.ToCycle, s.PlanId)
                        .With(string.Create(CultureInfo.InvariantCulture, $"from {scheduled.At:yyyy-MM-dd} ({Describe(scheduled.Source)})"), scheduled.Reason);
                    break;
                case SubscriptionChangeScheduled withdrawn:
                    yield return Line(s.TenantId, BillingEventKind.ChangeWithdrawn).ForPlan(s.PlanId, s.BillingCycle).With(Describe(withdrawn.Source), withdrawn.Reason);
                    break;
            }
        }

        if (entry.State != EntityState.Modified)
        {
            yield break;
        }

        if (Changed(entry, nameof(TenantSubscription.TrialEndsAt)) && s.Status == SubscriptionStatus.Trialing && s.TrialEndsAt is { } trialEnd)
        {
            yield return Line(s.TenantId, BillingEventKind.TrialExtended).ForPlan(s.PlanId, s.BillingCycle)
                .With(string.Create(CultureInfo.InvariantCulture, $"to {trialEnd:yyyy-MM-dd}"), s.StatusReason);
        }
        else if (Changed(entry, nameof(TenantSubscription.CurrentPeriodEnd)) && s.CurrentPeriodEnd > (DateTimeOffset)entry.Property(nameof(TenantSubscription.CurrentPeriodEnd)).OriginalValue!)
        {
            yield return Line(s.TenantId, BillingEventKind.PaidUpTo).ForPlan(s.PlanId, s.BillingCycle)
                .With(string.Create(CultureInfo.InvariantCulture, $"{s.CurrentPeriodEnd:yyyy-MM-dd}"), s.ExternalReference is { } reference ? "Reference " + reference : null);
        }

        if (Changed(entry, nameof(TenantSubscription.CancelAtPeriodEnd)) && !s.IsEnded)
        {
            yield return s.CancelAtPeriodEnd
                ? Line(s.TenantId, BillingEventKind.CancelScheduled).ForPlan(s.PlanId, s.BillingCycle).With(string.Create(CultureInfo.InvariantCulture, $"ends {s.CurrentPeriodEnd:yyyy-MM-dd}"), s.StatusReason)
                : Line(s.TenantId, BillingEventKind.Resumed).ForPlan(s.PlanId, s.BillingCycle);
        }
    }

    /// <param name="provider">Who settled it when no person is signed in: the payment provider's code.</param>
    private BillingEvent Line(Guid tenantId, BillingEventKind kind, string? provider = null)
    {
        var person = currentUser.UserId;
        var name = person is null && provider is not null ? provider : currentUser.AuditName;
        return new BillingEvent(tenantId, kind, time.GetUtcNow(), person, name).With(traceId: Activity.Current?.TraceId.ToString());
    }

    private static bool Changed(EntityEntry entry, string property)
    {
        var p = entry.Property(property);
        return entry.State == EntityState.Modified && !Equals(p.OriginalValue, p.CurrentValue);
    }

    private static string Describe(PlanChangeSource source) => source switch
    {
        PlanChangeSource.Operator => "by the platform",
        PlanChangeSource.Tenant => "by the organisation",
        PlanChangeSource.Payment => "paid for",
        PlanChangeSource.Upgrade => "upgrade paid for",
        _ => "as scheduled",
    };

    private static string SubscriptionStatusLabel(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Trialing => "Trial",
        SubscriptionStatus.PastDue => "Past due",
        _ => status.ToString(),
    };
}
