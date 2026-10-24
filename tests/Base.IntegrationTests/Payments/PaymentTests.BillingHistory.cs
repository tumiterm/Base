using Base.Application.Common.Abstractions;
using Base.Application.Payments;
using Base.Application.Subscriptions;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Payments;

/// <summary>The billing history: what happened, who did it and why, kept for good and never changed.</summary>
public sealed partial class PaymentTests
{
    [Fact]
    public async Task The_billing_history_says_who_did_what_and_why_and_cannot_be_changed()
    {
        var redirect = (await Pay(new PayRequest(_starter, BillingCycle.Monthly, "Test"))).GetDataOrThrow();
        var token = Uri.UnescapeDataString(redirect.Url.Query[(redirect.Url.Query.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        await AsNobodyAsync(async () =>
        {
            await ReceiveAsync("test", TestForm(token, "pay", "h-1"));
            await DeliverAsync();
        });
        (await Operator(s => s.ChangePlanAsync(_tenant, new ChangePlanRequest(_pro, BillingCycle.Monthly, Reason: "Agreed with sales")))).IsSuccess.ShouldBeTrue();

        var history = (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IBillingHistory>().ListAsync(_tenant))).GetDataOrThrow().Items;
        history.Select(e => e.Kind).Reverse().ShouldBe(
        [
            BillingEventKind.Subscribed,
            BillingEventKind.CheckoutStarted,
            BillingEventKind.PaymentReceived,
            BillingEventKind.StatusChanged,
            BillingEventKind.PaidUpTo,
            BillingEventKind.PlanChanged,
        ]);
        history.Single(e => e.Kind == BillingEventKind.CheckoutStarted).ShouldSatisfyAllConditions(
            e => e.ActorName.ShouldBe("Test Actor"),
            e => e.Title.ShouldBe("Payment of ZAR 100.00 started with Test payments"),
            e => e.PaymentReference.ShouldNotBeNull());
        history.Single(e => e.Kind == BillingEventKind.PaymentReceived).ActorName.ShouldBe("Test payments", "the provider settled it, not a person");
        history.Single(e => e.Kind == BillingEventKind.PlanChanged).ShouldSatisfyAllConditions(
            e => e.Title.ShouldBe("Plan changed from STARTER to PRO, monthly"),
            e => e.Reason.ShouldBe("Agreed with sales"),
            e => e.Detail.ShouldBe("by the platform"));
        (await _app.InScopeAsync(null, sp => sp.GetRequiredService<IBillingHistory>().ListMineAsync())).IsError.ShouldBeTrue("no organisation, no history of its own");
        (await _app.InScopeAsync(_tenant, sp => sp.GetRequiredService<IBillingHistory>().ListMineAsync())).GetDataOrThrow().Total.ShouldBe(history.Count);

        await Should.ThrowAsync<InvalidOperationException>(() => _app.InScopeAsync(_tenant, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var line = await db.BillingEvents.FirstAsync();
            db.Entry(line).Property(nameof(BillingEvent.Reason)).CurrentValue = "rewritten";
            return await db.SaveChangesAsync();
        }));

        // Years later, the audit trail's retention keeps the payments' and subscription's history.
        _app.Time.Advance(TimeSpan.FromDays(3 * 365));
        await _app.InScopeAsync(null, sp => sp.GetServices<Base.Application.Operations.IScheduledJob>().OfType<Base.Infrastructure.Jobs.AuditRetentionJob>().Single().PurgeAsync(CancellationToken.None));
        var kept = await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().AuditEntries.IgnoreQueryFilters().Select(e => e.EntityType).Distinct().ToListAsync());
        kept.ShouldContain(nameof(Payment));
        kept.ShouldContain(nameof(TenantSubscription));
    }

    /// <summary>As a gateway's notification arrives: nobody signed in.</summary>
    private async Task AsNobodyAsync(Func<Task> work)
    {
        var (id, name) = (_actor.UserId, _actor.AuditName);
        _actor.UserId = null;
        _actor.AuditName = ICurrentUser.SystemName;
        try
        {
            await work();
        }
        finally
        {
            (_actor.UserId, _actor.AuditName) = (id, name);
        }
    }
}
