using Base.Application.Payments;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Payments;

/// <summary>The platform's payment totals: real money only, by the month of the display time zone.</summary>
public sealed partial class PaymentTests
{
    [Fact]
    public async Task Received_this_month_counts_real_payments_by_the_display_time_zone_and_test_payments_apart()
    {
        // The default display time zone is Africa/Johannesburg (UTC+2): its month starts two hours before UTC's.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");
        var local = TimeZoneInfo.ConvertTime(_app.Time.GetUtcNow().AddHours(1), zone);
        var firstOfNext = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
        var boundary = new DateTimeOffset(firstOfNext, zone.GetUtcOffset(firstOfNext));

        _app.Time.Advance(boundary.AddMinutes(-30) - _app.Time.GetUtcNow());
        var lastMonth = await PendingReferenceAsync("Scripted");
        (await ReceiveAsync("scripted", Form(lastMonth, "paid", "t1"))).StatusCode.ShouldBe(200);

        _app.Time.Advance(TimeSpan.FromHours(1));
        _app.Time.GetUtcNow().Month.ShouldNotBe(firstOfNext.Month, "still last month in UTC");
        var thisMonth = await PendingReferenceAsync("Scripted");
        (await ReceiveAsync("scripted", Form(thisMonth, "paid", "t2"))).StatusCode.ShouldBe(200);
        var declined = await PendingReferenceAsync("Scripted");
        (await ReceiveAsync("scripted", Form(declined, "failed", "t3"))).StatusCode.ShouldBe(200);
        var test = await PaidAsync(new PayRequest(_pro, BillingCycle.Monthly, "Test"), "t4");
        await DeliverAsync();

        var totals = (await Platform(p => p.TotalsAsync())).GetDataOrThrow();
        totals.ShouldSatisfyAllConditions(
            t => t.MonthStart.ShouldBe(boundary),
            t => t.ReceivedThisMonth.ShouldHaveSingleItem().ShouldBe(new MoneyTotal("ZAR", 300m, 1)),
            t => t.ReceivedLastMonth.ShouldHaveSingleItem().ShouldBe(new MoneyTotal("ZAR", 300m, 1)),
            t => t.TestThisMonth.ShouldHaveSingleItem().ShouldBe(new MoneyTotal("ZAR", 300m, 1), "the Test gateway takes no money"),
            t => t.FailedThisMonth.ShouldBe(1));

        var tests = (await Platform(p => p.ListAllAsync(new PaymentQuery(Test: true)))).GetDataOrThrow();
        tests.Items.ShouldHaveSingleItem().ShouldSatisfyAllConditions(p => p.Reference.ShouldBe(test), p => p.IsTest.ShouldBeTrue());
        (await Platform(p => p.ListAllAsync(new PaymentQuery(PaymentStatus.Succeeded, Test: false)))).GetDataOrThrow().Items
            .Select(p => p.Reference).ShouldBe([thisMonth, lastMonth], ignoreOrder: true);
        (await Platform(p => p.ListAllAsync(new PaymentQuery(Search: "acm")))).GetDataOrThrow().Total.ShouldBe(4, "found by organisation");
        (await Platform(p => p.ListAllAsync(new PaymentQuery(Search: declined)))).GetDataOrThrow().Items.ShouldHaveSingleItem().Status.ShouldBe(PaymentStatus.Failed);
        (await Platform(p => p.ListAllAsync(new PaymentQuery(Gateway: "Test")))).GetDataOrThrow().Total.ShouldBe(1);
    }

    private Task<T> Platform<T>(Func<IPaymentService, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<IPaymentService>()));
}
