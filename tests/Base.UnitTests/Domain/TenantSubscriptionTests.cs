using Base.Domain.Subscriptions;

namespace Base.UnitTests.Domain;

public sealed class TenantSubscriptionTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);
    private static readonly Guid TenantId = Guid.NewGuid();

    private static Plan Plan(int trialDays = 14) => new("starter", "Starter", "ZAR", 100m, 1000m, trialDays);

    [Fact]
    public void A_plan_with_a_trial_starts_trialing_and_one_without_starts_active_for_a_period()
    {
        var trial = TenantSubscription.Start(TenantId, Plan(), BillingCycle.Monthly, Start);
        trial.Status.ShouldBe(SubscriptionStatus.Trialing);
        trial.CurrentPeriodEnd.ShouldBe(Start.AddDays(14));

        var paid = TenantSubscription.Start(TenantId, Plan(trialDays: 0), BillingCycle.Annual, Start);
        paid.Status.ShouldBe(SubscriptionStatus.Active);
        paid.CurrentPeriodEnd.ShouldBe(Start.AddYears(1));
    }

    [Fact]
    public void An_unpaid_trial_goes_past_due_then_suspended_after_the_grace_period()
    {
        var subscription = TenantSubscription.Start(TenantId, Plan(), BillingCycle.Monthly, Start);

        subscription.Advance(Start.AddDays(13), Grace).ShouldBeFalse();
        subscription.Advance(Start.AddDays(14), Grace).ShouldBeTrue();
        subscription.Status.ShouldBe(SubscriptionStatus.PastDue);
        subscription.HasAccess.ShouldBeTrue("past due keeps access during the grace period");

        subscription.Advance(Start.AddDays(20), Grace).ShouldBeFalse();
        subscription.Advance(Start.AddDays(21), Grace).ShouldBeTrue();
        subscription.Status.ShouldBe(SubscriptionStatus.Suspended);
        subscription.HasAccess.ShouldBeFalse();
        subscription.DomainEvents.OfType<SubscriptionStatusChanged>().Select(e => e.To)
            .ShouldBe([SubscriptionStatus.PastDue, SubscriptionStatus.Suspended]);
    }

    [Fact]
    public void A_payment_makes_it_active_for_a_period_from_now_or_from_the_end_of_the_paid_period()
    {
        var subscription = TenantSubscription.Start(TenantId, Plan(), BillingCycle.Monthly, Start);

        subscription.RecordPayment(Start.AddDays(2), "inv-1");
        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.CurrentPeriodEnd.ShouldBe(Start.AddDays(2).AddMonths(1));
        subscription.ExternalReference.ShouldBe("inv-1");

        // Paying early adds to the period already paid for.
        subscription.RecordPayment(Start.AddDays(10));
        subscription.CurrentPeriodEnd.ShouldBe(Start.AddDays(2).AddMonths(2));
    }

    [Fact]
    public void Reinstating_after_the_period_ended_gives_a_fresh_grace_period()
    {
        var subscription = TenantSubscription.Start(TenantId, Plan(), BillingCycle.Monthly, Start);
        subscription.Advance(Start.AddDays(30), Grace);
        subscription.Advance(Start.AddDays(30), Grace);
        subscription.Status.ShouldBe(SubscriptionStatus.Suspended);

        subscription.Reinstate(Start.AddDays(40));
        subscription.Status.ShouldBe(SubscriptionStatus.PastDue);
        subscription.Advance(Start.AddDays(46), Grace).ShouldBeFalse("the grace period starts again at reinstatement");
        subscription.Advance(Start.AddDays(47), Grace).ShouldBeTrue();
    }

    [Fact]
    public void Cancelling_at_period_end_keeps_access_until_then_and_can_be_resumed()
    {
        var subscription = TenantSubscription.Start(TenantId, Plan(trialDays: 0), BillingCycle.Monthly, Start);
        subscription.Cancel(Start.AddDays(5), atPeriodEnd: true);
        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.CancelAtPeriodEnd.ShouldBeTrue();

        subscription.Resume(Start.AddDays(6));
        subscription.Cancel(Start.AddDays(7), atPeriodEnd: true);
        subscription.Advance(Start.AddMonths(1), Grace).ShouldBeTrue();
        subscription.Status.ShouldBe(SubscriptionStatus.Cancelled);
        subscription.EndedAt.ShouldBe(Start.AddMonths(1));
    }

    [Fact]
    public void A_trial_set_to_cancel_expires_instead_of_going_past_due()
    {
        var subscription = TenantSubscription.Start(TenantId, Plan(), BillingCycle.Monthly, Start);
        subscription.Cancel(Start, atPeriodEnd: true);

        subscription.Advance(Start.AddDays(14), Grace);

        subscription.Status.ShouldBe(SubscriptionStatus.Expired);
    }

    [Fact]
    public void The_trial_reminder_is_due_once_inside_the_notice_window_and_again_after_an_extension()
    {
        var notice = TimeSpan.FromDays(3);
        var subscription = TenantSubscription.Start(TenantId, Plan(), BillingCycle.Monthly, Start);

        subscription.TrialEndsSoon(Start.AddDays(10), notice).ShouldBeFalse();
        subscription.TrialEndsSoon(Start.AddDays(11), notice).ShouldBeTrue();
        subscription.MarkTrialReminderSent(Start.AddDays(11));
        subscription.TrialEndsSoon(Start.AddDays(12), notice).ShouldBeFalse();

        subscription.ExtendTrial(7, Start.AddDays(12));
        subscription.CurrentPeriodEnd.ShouldBe(Start.AddDays(21));
        subscription.TrialEndsSoon(Start.AddDays(18), notice).ShouldBeTrue();
    }

    [Fact]
    public void Plan_entitlements_are_updated_in_place()
    {
        var plan = Plan();
        plan.SetEntitlement("Limit.Users", 5);
        plan.SetEntitlement("Limit.Users", null);

        plan.Entitlements.ShouldHaveSingleItem().Value.ShouldBeNull();
        Should.Throw<ArgumentOutOfRangeException>(() => plan.SetEntitlement("Limit.Users", -1));
    }
}
