using Base.Domain.Payments;
using Base.Domain.Subscriptions;

namespace Base.UnitTests.Payments;

public sealed class PaymentRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_payment_has_a_short_readable_unique_reference_and_rounds_to_cents()
    {
        var a = NewPayment(amount: 299.999m);
        var b = NewPayment();
        a.Reference.Length.ShouldBe(Payment.ReferenceLength);
        a.Reference.ShouldStartWith("P");
        a.Reference.ShouldNotContain("O");
        a.Reference.ShouldNotBe(b.Reference);
        a.Amount.ShouldBe(300.00m);
        Should.Throw<ArgumentOutOfRangeException>(() => NewPayment(amount: 0m));
    }

    [Fact]
    public void Only_the_first_success_counts_and_it_wins_over_a_cancel_or_expiry()
    {
        var payment = NewPayment();
        payment.Cancel(Now).ShouldBeTrue();
        payment.Fail("late decline", Now).ShouldBeFalse("only a waiting payment can end another way");

        payment.Succeed("GW-1", Now).ShouldBeTrue("the money was taken, so it counts");
        payment.Succeed("GW-1", Now).ShouldBeFalse();
        payment.ShouldSatisfyAllConditions(
            p => p.Status.ShouldBe(PaymentStatus.Succeeded),
            p => p.GatewayReference.ShouldBe("GW-1"),
            p => p.FailureReason.ShouldBeNull(),
            p => p.DomainEvents.OfType<PaymentSucceeded>().ShouldHaveSingleItem());
    }

    [Fact]
    public void A_waiting_payment_expires_only_after_its_time()
    {
        var payment = NewPayment();
        payment.Expire(Now.AddHours(1)).ShouldBeFalse();
        payment.Expire(Now.AddHours(2)).ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Expired);
    }

    [Fact]
    public void Paying_for_the_same_plan_extends_and_another_plan_starts_today()
    {
        var starter = Plan("starter");
        var pro = Plan("pro");
        var subscription = TenantSubscription.Start(Guid.NewGuid(), starter, BillingCycle.Monthly, Now, withTrial: false);

        subscription.ApplyPayment(starter, BillingCycle.Monthly, Now.AddDays(10), "P1");
        subscription.CurrentPeriodEnd.ShouldBe(Now.AddMonths(2), "a month after the paid month");

        subscription.ApplyPayment(pro, BillingCycle.Annual, Now.AddDays(20), "P2");
        subscription.ShouldSatisfyAllConditions(
            s => s.PlanId.ShouldBe(pro.Id),
            s => s.BillingCycle.ShouldBe(BillingCycle.Annual),
            s => s.CurrentPeriodEnd.ShouldBe(Now.AddDays(20).AddYears(1)),
            s => s.ExternalReference.ShouldBe("P2"));
    }

    private static Payment NewPayment(decimal amount = 100m) =>
        new(Guid.NewGuid(), "Test", amount, "zar", "Pro, 1 month", Guid.NewGuid(), BillingCycle.Monthly, null, "Pat", Now, TimeSpan.FromHours(2));

    private static Plan Plan(string code) => new(code, code.ToUpperInvariant(), "ZAR", 100m, 1000m, 0);
}
