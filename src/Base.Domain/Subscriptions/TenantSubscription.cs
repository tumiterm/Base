using System.Globalization;
using Base.Domain.Common;

namespace Base.Domain.Subscriptions;

public enum SubscriptionStatus
{
    /// <summary>Free trial; full access until <see cref="TenantSubscription.TrialEndsAt"/>.</summary>
    Trialing = 0,

    /// <summary>Paid up to <see cref="TenantSubscription.CurrentPeriodEnd"/>.</summary>
    Active = 1,

    /// <summary>Trial ended or renewal not received. Still has access during the grace period.</summary>
    PastDue = 2,

    /// <summary>No access (unpaid after the grace period, or suspended by the operator). Data is kept.</summary>
    Suspended = 3,

    /// <summary>Ended by request. No access.</summary>
    Cancelled = 4,

    /// <summary>A trial that was set to end and did. No access.</summary>
    Expired = 5,
}

public enum BillingCycle
{
    Monthly = 0,
    Annual = 1,
}

/// <summary>Raised once per trial, a few days before it ends.</summary>
public sealed record SubscriptionTrialEnding(Guid TenantId, Guid SubscriptionId, Guid PlanId, DateTimeOffset EndsAt) : IDomainEvent;

/// <summary>Raised on every status change, so handlers can notify the tenant or tell a billing system.</summary>
public sealed record SubscriptionStatusChanged(Guid TenantId, Guid SubscriptionId, SubscriptionStatus From, SubscriptionStatus To, string? Reason) : IDomainEvent;

/// <summary>Who or what changed the plan.</summary>
public enum PlanChangeSource
{
    /// <summary>The platform's operator.</summary>
    Operator = 0,

    /// <summary>The organisation itself, without paying (a downgrade, a change of period, an upgrade with nothing to pay).</summary>
    Tenant = 1,

    /// <summary>A payment for another plan or period: it replaces what an automatic renewal paid for.</summary>
    Payment = 2,

    /// <summary>A change scheduled for the end of the period, taking effect.</summary>
    Schedule = 3,

    /// <summary>The difference for an upgrade, paid.</summary>
    Upgrade = 4,
}

/// <summary>Raised when the plan or billing cycle changes now, so renewals and notices can follow.</summary>
public sealed record SubscriptionPlanChanged(
    Guid TenantId, Guid SubscriptionId, Guid FromPlanId, BillingCycle FromCycle, Guid ToPlanId, BillingCycle ToCycle, PlanChangeSource Source, string? Reason)
    : IDomainEvent;

/// <summary>
/// Raised when a change is scheduled for the end of the period (<paramref name="ToPlanId"/> set) or the scheduled change
/// is withdrawn (null).
/// </summary>
public sealed record SubscriptionChangeScheduled(
    Guid TenantId, Guid SubscriptionId, Guid? ToPlanId, BillingCycle? ToCycle, DateTimeOffset? At, PlanChangeSource Source, string? Reason)
    : IDomainEvent;

/// <summary>
/// A tenant's subscription to a plan, and where it is in its lifecycle:
/// Trialing → Active → PastDue → Suspended, or Cancelled / Expired. One per tenant. Payments are recorded through
/// <see cref="RecordPayment"/> (by an operator or a billing integration); <see cref="Advance"/> moves it on as time passes.
/// </summary>
public sealed class TenantSubscription : EntityBase, ITenantOwned
{
    public const int ReasonMaxLength = 500;
    public const int ExternalReferenceMaxLength = 200;

    private TenantSubscription()
    {
    }

    private TenantSubscription(Guid tenantId, Plan plan, BillingCycle cycle, SubscriptionStatus status, DateTimeOffset now, DateTimeOffset periodEnd)
    {
        Id = SequentialGuid.Next();
        TenantId = tenantId;
        PlanId = plan.Id;
        BillingCycle = cycle;
        Status = status;
        StartedAt = now;
        StatusChangedAt = now;
        CurrentPeriodEnd = periodEnd;
    }

    public Guid TenantId { get; set; }

    public Guid PlanId { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public BillingCycle BillingCycle { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? TrialEndsAt { get; private set; }

    /// <summary>End of the trial or of the paid period.</summary>
    public DateTimeOffset CurrentPeriodEnd { get; private set; }

    /// <summary>Ends instead of renewing when the current period (or trial) is over.</summary>
    public bool CancelAtPeriodEnd { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    public DateTimeOffset? PastDueSince { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public string? StatusReason { get; private set; }

    /// <summary>When the "trial ends soon" notice went out, so it goes out once per trial.</summary>
    public DateTimeOffset? TrialReminderSentAt { get; private set; }

    /// <summary>The billing system's id for this subscription, if one is used.</summary>
    public string? ExternalReference { get; private set; }

    /// <summary>The plan it moves to at <see cref="ScheduledChangeAt"/> (a downgrade or change of period waits for the paid period to end).</summary>
    public Guid? ScheduledPlanId { get; private set; }

    public BillingCycle? ScheduledBillingCycle { get; private set; }

    /// <summary>When the scheduled change takes effect: the end of the period that was running when it was scheduled.</summary>
    public DateTimeOffset? ScheduledChangeAt { get; private set; }

    public bool HasScheduledChange => ScheduledPlanId is not null;

    /// <summary>Whether a paid period is running, so a change can wait for its end and an upgrade can be charged pro rata.</summary>
    public bool IsInPaidPeriod(DateTimeOffset now) => Status == SubscriptionStatus.Active && CurrentPeriodEnd > now;

    /// <summary>Whether the tenant can use the application (trialing, active, or past due within grace).</summary>
    public bool HasAccess => Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    public bool IsEnded => Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired;

    /// <summary>Starts with the plan's trial, or active for one billing period when the plan has no trial.</summary>
    public static TenantSubscription Start(Guid tenantId, Plan plan, BillingCycle cycle, DateTimeOffset now, bool withTrial = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (withTrial && plan.TrialDays > 0)
        {
            var trialEnd = now.AddDays(plan.TrialDays);
            return new TenantSubscription(tenantId, plan, cycle, SubscriptionStatus.Trialing, now, trialEnd) { TrialEndsAt = trialEnd };
        }

        return new TenantSubscription(tenantId, plan, cycle, SubscriptionStatus.Active, now, NextPeriodEnd(now, cycle));
    }

    /// <summary>Switches plan and cycle now, keeping the period; any scheduled change is dropped.</summary>
    public void ChangePlan(Plan plan, BillingCycle cycle, PlanChangeSource source, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        DropScheduledChange(source, reason);
        if (plan.Id == PlanId && cycle == BillingCycle)
        {
            return;
        }

        var (fromPlan, fromCycle) = (PlanId, BillingCycle);
        PlanId = plan.Id;
        BillingCycle = cycle;
        Raise(new SubscriptionPlanChanged(TenantId, Id, fromPlan, fromCycle, plan.Id, cycle, source, Trim(reason)));
    }

    /// <summary>
    /// Moves to <paramref name="plan"/> and <paramref name="cycle"/> when the running period (or trial) ends. Choosing the
    /// current plan and cycle withdraws a scheduled change.
    /// </summary>
    public void ScheduleChange(Plan plan, BillingCycle cycle, DateTimeOffset now, PlanChangeSource source, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!HasAccess || CurrentPeriodEnd <= now)
        {
            throw new InvalidOperationException("A change can only wait for a period that is still running.");
        }

        if (CancelAtPeriodEnd)
        {
            throw new InvalidOperationException("The subscription ends with this period; there is nothing to change to.");
        }

        if (plan.Id == PlanId && cycle == BillingCycle)
        {
            DropScheduledChange(source, reason);
            return;
        }

        ScheduledPlanId = plan.Id;
        ScheduledBillingCycle = cycle;
        ScheduledChangeAt = CurrentPeriodEnd;
        Raise(new SubscriptionChangeScheduled(TenantId, Id, plan.Id, cycle, ScheduledChangeAt, source, Trim(reason)));
    }

    /// <summary>Keeps the current plan after all.</summary>
    public void CancelScheduledChange(PlanChangeSource source, string? reason = null) => DropScheduledChange(source, reason);

    /// <summary>
    /// An upgrade paid for (or with nothing to pay) part-way through a period: the new plan applies now and the period
    /// stays as it was.
    /// </summary>
    public void Upgrade(Plan plan, PlanChangeSource source, string? externalReference = null)
    {
        ChangePlan(plan, BillingCycle, source, "Upgraded");
        if (!string.IsNullOrWhiteSpace(externalReference))
        {
            ExternalReference = externalReference.Trim();
        }
    }

    /// <summary>
    /// A payment for the next period (by the operator, or charged by an automatic renewal): active until one cycle past
    /// the later of now and the current period end. A scheduled change still happens when it is due: the period paid
    /// for after it is on the scheduled plan and cycle.
    /// </summary>
    public void RecordPayment(DateTimeOffset now, string? externalReference = null)
    {
        ApplyDueChange(now);
        var from = CurrentPeriodEnd > now && Status is SubscriptionStatus.Active ? CurrentPeriodEnd : now;
        var cycle = HasScheduledChange && ScheduledChangeAt <= from ? ScheduledBillingCycle!.Value : BillingCycle;
        CurrentPeriodEnd = NextPeriodEnd(from, cycle);
        TrialEndsAt = Status == SubscriptionStatus.Trialing ? now : TrialEndsAt;
        CancelAtPeriodEnd = false;
        EndedAt = null;
        if (!string.IsNullOrWhiteSpace(externalReference))
        {
            ExternalReference = externalReference.Trim();
        }

        MoveTo(SubscriptionStatus.Active, now, "Payment recorded");
    }

    /// <summary>
    /// A payment the organisation made for <paramref name="plan"/> and <paramref name="cycle"/>: it chose what it pays
    /// for, so a scheduled change is dropped. The same plan and cycle extend the subscription like
    /// <see cref="RecordPayment"/>; a different one switches to it with a fresh period from <paramref name="now"/>.
    /// </summary>
    public void ApplyPayment(Plan plan, BillingCycle cycle, DateTimeOffset now, string reference)
    {
        ArgumentNullException.ThrowIfNull(plan);
        DropScheduledChange(PlanChangeSource.Payment, null);
        if (plan.Id != PlanId || cycle != BillingCycle)
        {
            ChangePlan(plan, cycle, PlanChangeSource.Payment);
            CurrentPeriodEnd = now;
        }

        RecordPayment(now, reference);
    }

    /// <summary>Whether the trial ends within <paramref name="notice"/> and nobody has been told yet.</summary>
    public bool TrialEndsSoon(DateTimeOffset now, TimeSpan notice) =>
        Status == SubscriptionStatus.Trialing && !CancelAtPeriodEnd && TrialReminderSentAt is null && CurrentPeriodEnd > now && CurrentPeriodEnd - notice <= now;

    public void MarkTrialReminderSent(DateTimeOffset now)
    {
        TrialReminderSentAt = now;
        Raise(new SubscriptionTrialEnding(TenantId, Id, PlanId, CurrentPeriodEnd));
    }

    /// <summary>Extends a trial by a number of days (e.g. a sales-led extension).</summary>
    public void ExtendTrial(int days, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);
        if (Status != SubscriptionStatus.Trialing)
        {
            throw new InvalidOperationException("Only a trial can be extended.");
        }

        TrialEndsAt = (TrialEndsAt > now ? TrialEndsAt.Value : now).AddDays(days);
        TrialReminderSentAt = null;
        CurrentPeriodEnd = TrialEndsAt.Value;
        StatusReason = string.Create(CultureInfo.InvariantCulture, $"Trial extended by {days} day(s)");
        StatusChangedAt = now;
    }

    public void Suspend(DateTimeOffset now, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (IsEnded)
        {
            throw new InvalidOperationException("An ended subscription can't be suspended.");
        }

        MoveTo(SubscriptionStatus.Suspended, now, reason);
    }

    /// <summary>Lifts a suspension: active again if the paid period is still running, otherwise past due.</summary>
    public void Reinstate(DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Suspended)
        {
            throw new InvalidOperationException("Only a suspended subscription can be reinstated.");
        }

        var target = CurrentPeriodEnd > now
            ? (TrialEndsAt is { } trial && trial >= CurrentPeriodEnd ? SubscriptionStatus.Trialing : SubscriptionStatus.Active)
            : SubscriptionStatus.PastDue;
        if (target == SubscriptionStatus.PastDue)
        {
            PastDueSince = now; // a fresh grace period to pay
        }

        MoveTo(target, now, "Reinstated");
    }

    /// <summary>Cancels now, or at the end of the current period (the default: they've paid for it).</summary>
    public void Cancel(DateTimeOffset now, bool atPeriodEnd, string? reason = null)
    {
        if (IsEnded)
        {
            return;
        }

        DropScheduledChange(PlanChangeSource.Tenant, "The subscription is cancelled");
        if (atPeriodEnd && HasAccess)
        {
            CancelAtPeriodEnd = true;
            StatusReason = reason ?? "Cancels at the end of the period";
            StatusChangedAt = now;
            return;
        }

        EndedAt = now;
        MoveTo(SubscriptionStatus.Cancelled, now, reason ?? "Cancelled");
    }

    /// <summary>Undoes a cancel-at-period-end before the period is over.</summary>
    public void Resume(DateTimeOffset now)
    {
        if (!CancelAtPeriodEnd || IsEnded)
        {
            throw new InvalidOperationException("Only a subscription set to cancel can be resumed.");
        }

        CancelAtPeriodEnd = false;
        StatusReason = "Resumed";
        StatusChangedAt = now;
    }

    /// <summary>
    /// Applies the passage of time: an ended trial or unpaid renewal becomes past due (or ends, when set to cancel),
    /// and past due for longer than <paramref name="gracePeriod"/> becomes suspended. Returns whether anything changed.
    /// </summary>
    public bool Advance(DateTimeOffset now, TimeSpan gracePeriod)
    {
        if (ApplyDueChange(now))
        {
            return true;
        }

        switch (Status)
        {
            case SubscriptionStatus.Trialing when CurrentPeriodEnd <= now:
                if (CancelAtPeriodEnd)
                {
                    EndedAt = CurrentPeriodEnd;
                    MoveTo(SubscriptionStatus.Expired, now, "Trial ended");
                }
                else
                {
                    PastDueSince = CurrentPeriodEnd;
                    MoveTo(SubscriptionStatus.PastDue, now, "Trial ended without payment");
                }

                return true;

            case SubscriptionStatus.Active when CurrentPeriodEnd <= now:
                if (CancelAtPeriodEnd)
                {
                    EndedAt = CurrentPeriodEnd;
                    MoveTo(SubscriptionStatus.Cancelled, now, "Cancelled at the end of the period");
                }
                else
                {
                    PastDueSince = CurrentPeriodEnd;
                    MoveTo(SubscriptionStatus.PastDue, now, "Renewal payment not received");
                }

                return true;

            case SubscriptionStatus.PastDue when (PastDueSince ?? StatusChangedAt) + gracePeriod <= now:
                MoveTo(SubscriptionStatus.Suspended, now, string.Create(CultureInfo.InvariantCulture, $"Unpaid for more than {gracePeriod.TotalDays:0} day(s)"));
                return true;

            default:
                return false;
        }
    }

    /// <summary>The start of the running period: one cycle before its end.</summary>
    public DateTimeOffset CurrentPeriodStart => BillingCycle == BillingCycle.Annual ? CurrentPeriodEnd.AddYears(-1) : CurrentPeriodEnd.AddMonths(-1);

    private static DateTimeOffset NextPeriodEnd(DateTimeOffset from, BillingCycle cycle) =>
        cycle == BillingCycle.Annual ? from.AddYears(1) : from.AddMonths(1);

    private static string? Trim(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > ReasonMaxLength ? trimmed[..ReasonMaxLength] : trimmed;
    }

    /// <summary>Makes a scheduled change whose time has come. Returns whether it did.</summary>
    private bool ApplyDueChange(DateTimeOffset now)
    {
        if (ScheduledPlanId is not { } planId || ScheduledChangeAt > now)
        {
            return false;
        }

        var (fromPlan, fromCycle) = (PlanId, BillingCycle);
        PlanId = planId;
        BillingCycle = ScheduledBillingCycle ?? BillingCycle;
        ClearSchedule();
        Raise(new SubscriptionPlanChanged(TenantId, Id, fromPlan, fromCycle, PlanId, BillingCycle, PlanChangeSource.Schedule, "Scheduled change"));
        return true;
    }

    private void DropScheduledChange(PlanChangeSource source, string? reason)
    {
        if (!HasScheduledChange)
        {
            return;
        }

        ClearSchedule();
        Raise(new SubscriptionChangeScheduled(TenantId, Id, null, null, null, source, Trim(reason)));
    }

    private void ClearSchedule()
    {
        ScheduledPlanId = null;
        ScheduledBillingCycle = null;
        ScheduledChangeAt = null;
    }

    private void MoveTo(SubscriptionStatus status, DateTimeOffset now, string reason)
    {
        var previous = Status;
        Status = status;
        StatusChangedAt = now;
        StatusReason = reason.Length > ReasonMaxLength ? reason[..ReasonMaxLength] : reason;
        if (status != SubscriptionStatus.PastDue && status != SubscriptionStatus.Suspended)
        {
            PastDueSince = null;
        }

        if (previous != status)
        {
            Raise(new SubscriptionStatusChanged(TenantId, Id, previous, status, StatusReason));
        }
    }
}
