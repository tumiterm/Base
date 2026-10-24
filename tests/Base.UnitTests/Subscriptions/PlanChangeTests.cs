using Base.Application.Subscriptions;
using Base.Domain.Subscriptions;

namespace Base.UnitTests.Subscriptions;

public sealed class PlanChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly Plan _starter = new("starter", "Starter", "ZAR", 100m, 1000m, 0);
    private readonly Plan _pro = new("pro", "Pro", "ZAR", 300m, 3000m, 0);

    [Fact]
    public void An_upgrade_pays_the_difference_for_what_is_left_of_the_period()
    {
        var subscription = Active(_starter);
        var halfway = subscription.CurrentPeriodStart + ((subscription.CurrentPeriodEnd - subscription.CurrentPeriodStart) / 2);

        PlanChangeRules.Classify(subscription, Summary(_starter), Summary(_pro), BillingCycle.Monthly, halfway)
            .ShouldBe((PlanChangeKind.Upgrade, halfway, 100.00m), "half of the R200 difference");
        PlanChangeRules.Classify(subscription, Summary(_starter), Summary(_pro), BillingCycle.Monthly, Now)
            .AmountDueNow.ShouldBe(200.00m, "all of the period is left");
        PlanChangeRules.ProRata(200m, subscription, subscription.CurrentPeriodEnd).ShouldBe(0m);

        subscription.RecordPayment(Now, "ahead");
        PlanChangeRules.Classify(subscription, Summary(_starter), Summary(_pro), BillingCycle.Monthly, Now)
            .AmountDueNow.ShouldBeInRange(390m, 410m, "two months are paid for, so the difference is for both (months differ in length)");
    }

    [Fact]
    public void A_downgrade_or_another_cycle_waits_for_the_end_of_the_period_and_without_one_any_plan_starts_today()
    {
        var subscription = Active(_pro);
        PlanChangeRules.Classify(subscription, Summary(_pro), Summary(_starter), BillingCycle.Monthly, Now)
            .ShouldBe((PlanChangeKind.AtPeriodEnd, subscription.CurrentPeriodEnd, 0m));
        PlanChangeRules.Classify(subscription, Summary(_pro), Summary(_pro), BillingCycle.Annual, Now).Kind.ShouldBe(PlanChangeKind.AtPeriodEnd);
        PlanChangeRules.Classify(subscription, Summary(_pro), Summary(_pro), BillingCycle.Monthly, Now)
            .ShouldBe((PlanChangeKind.Extend, subscription.CurrentPeriodEnd, 300m), "the same plan pays for the next period");

        var trial = TenantSubscription.Start(Guid.NewGuid(), Plan(_pro, trialDays: 14), BillingCycle.Monthly, Now);
        PlanChangeRules.Classify(trial, Summary(_pro), Summary(_starter), BillingCycle.Annual, Now)
            .ShouldBe((PlanChangeKind.Start, Now, 1000m), "a trial isn't a paid period");
    }

    [Fact]
    public void Limits_on_what_is_kept_block_a_smaller_plan_but_monthly_ones_do_not()
    {
        var small = Summary(_starter, new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            [LimitCodes.Users] = 2,
            [LimitCodes.StorageMb] = null,
            [LimitCodes.MessagesPerMonth] = 10,
        });
        var used = new Dictionary<string, long>(StringComparer.Ordinal) { [LimitCodes.Users] = 5, [LimitCodes.StorageMb] = 1L << 40, [LimitCodes.MessagesPerMonth] = 500 };

        var over = PlanChangeRules.OverLimits(small, used).ShouldHaveSingleItem();
        over.ShouldSatisfyAllConditions(o => o.Code.ShouldBe(LimitCodes.Users), o => o.Over.ShouldBe(3));
        over.Describe().ShouldBe("5 users; the plan allows 2. Lock or remove 3 first (locked users don't count).");
    }

    [Fact]
    public void A_scheduled_change_happens_when_the_period_ends_and_a_payment_meanwhile_pays_for_the_new_cycle()
    {
        var subscription = Active(_pro);
        var end = subscription.CurrentPeriodEnd;
        subscription.ScheduleChange(_starter, BillingCycle.Annual, Now, PlanChangeSource.Tenant);
        subscription.ShouldSatisfyAllConditions(
            s => s.ScheduledPlanId.ShouldBe(_starter.Id),
            s => s.ScheduledChangeAt.ShouldBe(end),
            s => s.DomainEvents.OfType<SubscriptionChangeScheduled>().ShouldHaveSingleItem().ToPlanId.ShouldBe(_starter.Id));

        // An automatic renewal charged a day early pays for the next period, which is the new plan's year.
        subscription.RecordPayment(end.AddDays(-1), "R-1");
        subscription.ShouldSatisfyAllConditions(
            s => s.PlanId.ShouldBe(_pro.Id, "not before its date"),
            s => s.CurrentPeriodEnd.ShouldBe(end.AddYears(1)));

        subscription.Advance(end, TimeSpan.FromDays(7)).ShouldBeTrue();
        subscription.ShouldSatisfyAllConditions(
            s => s.PlanId.ShouldBe(_starter.Id),
            s => s.BillingCycle.ShouldBe(BillingCycle.Annual),
            s => s.HasScheduledChange.ShouldBeFalse(),
            s => s.Status.ShouldBe(SubscriptionStatus.Active),
            s => s.DomainEvents.OfType<SubscriptionPlanChanged>().ShouldHaveSingleItem().Source.ShouldBe(PlanChangeSource.Schedule));
    }

    [Fact]
    public void Choosing_the_current_plan_paying_for_a_plan_or_cancelling_withdraws_a_scheduled_change()
    {
        var kept = Active(_pro);
        kept.ScheduleChange(_starter, BillingCycle.Monthly, Now, PlanChangeSource.Tenant);
        kept.ScheduleChange(_pro, BillingCycle.Monthly, Now, PlanChangeSource.Tenant);
        kept.HasScheduledChange.ShouldBeFalse();

        var paid = Active(_pro);
        paid.ScheduleChange(_starter, BillingCycle.Monthly, Now, PlanChangeSource.Tenant);
        paid.ApplyPayment(_pro, BillingCycle.Monthly, Now.AddDays(1), "P-1");
        paid.ShouldSatisfyAllConditions(s => s.HasScheduledChange.ShouldBeFalse("they paid for Pro"), s => s.CurrentPeriodEnd.ShouldBe(Now.AddMonths(2)));

        var cancelled = Active(_pro);
        cancelled.ScheduleChange(_starter, BillingCycle.Monthly, Now, PlanChangeSource.Tenant);
        cancelled.Cancel(Now, atPeriodEnd: true);
        cancelled.HasScheduledChange.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => cancelled.ScheduleChange(_starter, BillingCycle.Monthly, Now, PlanChangeSource.Tenant));
    }

    [Fact]
    public void An_upgrade_keeps_the_period_and_tells_what_changed()
    {
        var subscription = Active(_starter);
        var end = subscription.CurrentPeriodEnd;
        subscription.Upgrade(_pro, PlanChangeSource.Upgrade, "P-9");
        subscription.ShouldSatisfyAllConditions(
            s => s.PlanId.ShouldBe(_pro.Id),
            s => s.CurrentPeriodEnd.ShouldBe(end),
            s => s.ExternalReference.ShouldBe("P-9"));
        var changed = subscription.DomainEvents.OfType<SubscriptionPlanChanged>().ShouldHaveSingleItem();
        changed.ShouldSatisfyAllConditions(e => e.FromPlanId.ShouldBe(_starter.Id), e => e.ToPlanId.ShouldBe(_pro.Id), e => e.Source.ShouldBe(PlanChangeSource.Upgrade));
    }

    private static TenantSubscription Active(Plan plan) => TenantSubscription.Start(Guid.NewGuid(), plan, BillingCycle.Monthly, Now, withTrial: false);

    private static Plan Plan(Plan like, int trialDays) => new(like.Code, like.Name, like.Currency, like.MonthlyPrice, like.AnnualPrice, trialDays);

    private static PlanSummary Summary(Plan plan, IReadOnlyDictionary<string, long?>? entitlements = null) =>
        new(plan.Id, plan.Code, plan.Name, null, plan.Currency, plan.MonthlyPrice, plan.AnnualPrice, plan.TrialDays, true, 0, 0,
            entitlements ?? new Dictionary<string, long?>(StringComparer.Ordinal));
}
