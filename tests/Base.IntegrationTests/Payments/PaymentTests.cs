using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Operations;
using Base.Application.Payments;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Domain.Notifications;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Base.Infrastructure.Outbox;
using Base.Infrastructure.Persistence;
using Base.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.Payments;

/// <summary>Paying for a subscription through the built-in Test gateway and a scripted one, end to end on SQL Server.</summary>
public sealed class PaymentTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly PaymentUrls Urls = new(new Uri("https://acme.example.com/subscription/payments"), new Uri("https://base.example.com/payments/notify"));

    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private TestApp _app = null!;
    private Guid _starter;
    private Guid _pro;
    private Guid _tenant;

    public async ValueTask InitializeAsync()
    {
        _app = await TestApp.CreateAsync(
            sql.NewDatabase(),
            services =>
            {
                services.AddSingleton<ICurrentUser>(_actor);
                services.AddScoped<IPaymentGateway, ScriptedGateway>();
            },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Payments:Gateways:Test:Enabled"] = "true" });
        _starter = await PlanAsync("starter", 100m, 1000m);
        _pro = await PlanAsync("pro", 300m, 3000m);
        _tenant = (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<ITenantAdministration>()
            .CreateAsync(new CreateTenantRequest("Acme", "acme", null, "owner@acme.test", "Olu Owner", _starter)))).GetDataOrThrow();
        await DeliverAsync();
        _app.Sent.Clear();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Paying_for_another_plan_switches_to_it_for_a_fresh_period_once_however_often_the_gateway_says_so()
    {
        var redirect = (await Pay(new PayRequest(_pro, BillingCycle.Annual, "Test"))).GetDataOrThrow();
        redirect.IsFormPost.ShouldBeFalse();
        redirect.Url.AbsoluteUri.ShouldStartWith("https://acme.example.com/payments/test?token=");
        var token = Uri.UnescapeDataString(redirect.Url.Query[(redirect.Url.Query.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        var checkout = _app.Services.CreateScope().ServiceProvider.GetRequiredService<ITestCheckout>().Read(token)!;
        checkout.Amount.ShouldBe(3000m);
        checkout.ReturnUrl.AbsoluteUri.ShouldBe("https://acme.example.com/subscription/payments/" + checkout.Reference);

        var paid = TestForm(token, "pay", "evt-1");
        (await ReceiveAsync("test", paid)).StatusCode.ShouldBe(200);
        (await ReceiveAsync("test", paid)).StatusCode.ShouldBe(200, "a repeat is acknowledged and changes nothing");
        await DeliverAsync();

        var subscription = await SubscriptionAsync();
        subscription.ShouldSatisfyAllConditions(
            s => s.PlanId.ShouldBe(_pro),
            s => s.BillingCycle.ShouldBe(BillingCycle.Annual),
            s => s.Status.ShouldBe(SubscriptionStatus.Active),
            s => s.CurrentPeriodEnd.ShouldBe(_app.Time.GetUtcNow().AddYears(1)),
            s => s.ExternalReference.ShouldBe(checkout.Reference));

        var payment = (await Payments(p => p.ReturnedAsync(checkout.Reference, cancelled: false))).GetDataOrThrow();
        payment.ShouldSatisfyAllConditions(p => p.Status.ShouldBe(PaymentStatus.Succeeded), p => p.Applied.ShouldBeTrue(), p => p.GatewayName.ShouldBe("Test payments"));
        var receipt = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem("the payer here has no account; the organisation's contact gets the receipt");
        receipt.To.ShouldBe("owner@acme.test");
        receipt.Subject.ShouldBe("Payment received: ZAR 3,000.00 for PRO");
        receipt.Body!.ShouldContain(checkout.Reference);

        var log = await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().PaymentNotifications.Select(n => n.Outcome).ToListAsync());
        log.ShouldBe([PaymentNotificationOutcome.Applied]);

        // Paying again for the same plan and cycle adds a period after the current one.
        var again = (await Pay(new PayRequest(_pro, BillingCycle.Annual, "Test"))).GetDataOrThrow();
        var secondToken = Uri.UnescapeDataString(again.Url.Query[(again.Url.Query.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        await ReceiveAsync("test", TestForm(secondToken, "pay", "evt-2"));
        await DeliverAsync();
        (await SubscriptionAsync()).CurrentPeriodEnd.ShouldBe(_app.Time.GetUtcNow().AddYears(2));
    }

    [Fact]
    public async Task Notifications_that_arent_verified_or_dont_match_the_payment_change_nothing()
    {
        var reference = await PendingReferenceAsync("Scripted");

        (await ReceiveAsync("scripted", Form(reference, "paid", "e1", verified: false))).StatusCode.ShouldBe(400);
        (await ReceiveAsync("scripted", Form(reference, "paid", "e2", amount: "1.00"))).StatusCode.ShouldBe(400);
        (await ReceiveAsync("scripted", Form("PNOTOURS1234", "paid", "e3"))).StatusCode.ShouldBe(200);
        (await ReceiveAsync("nobody", Form(reference, "paid", "e4"))).StatusCode.ShouldBe(404);
        (await StatusAsync(reference)).ShouldBe(PaymentStatus.Pending);

        var outcomes = await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().PaymentNotifications.Select(n => n.Outcome).ToListAsync());
        outcomes.ShouldBe([PaymentNotificationOutcome.Rejected, PaymentNotificationOutcome.Rejected, PaymentNotificationOutcome.Ignored], ignoreOrder: true, "the unverified one and the wrong amount are rejected; the stranger is ignored");

        (await ReceiveAsync("scripted", Form(reference, "failed", "e5"))).StatusCode.ShouldBe(200);
        (await StatusAsync(reference)).ShouldBe(PaymentStatus.Failed);
    }

    [Fact]
    public async Task A_notification_that_names_only_the_gateways_checkout_still_finds_its_payment()
    {
        var reference = await PendingReferenceAsync("Scripted");
        var form = Form("ignored", "paid", "c1");
        form["checkout"] = "CHK-" + reference;
        (await ReceiveAsync("scripted", form)).StatusCode.ShouldBe(200);
        (await StatusAsync(reference)).ShouldBe(PaymentStatus.Succeeded);
    }

    [Fact]
    public async Task Leaving_the_gateway_cancels_but_a_late_success_still_counts_and_unanswered_payments_expire()
    {
        var left = await PendingReferenceAsync("Scripted");
        (await Payments(p => p.ReturnedAsync(left, cancelled: true))).GetDataOrThrow().Status.ShouldBe(PaymentStatus.Cancelled);
        await ReceiveAsync("scripted", Form(left, "paid", "late"));
        await DeliverAsync();
        (await StatusAsync(left)).ShouldBe(PaymentStatus.Succeeded);
        (await SubscriptionAsync()).Status.ShouldBe(SubscriptionStatus.Active);

        var waiting = await PendingReferenceAsync("Scripted");
        _app.Time.Advance(TimeSpan.FromHours(3));
        var result = await _app.InScopeAsync(null, sp => sp.GetServices<IScheduledJob>().Single(j => j.Code == "payment-expiry").RunAsync(CancellationToken.None));
        result.ShouldBe("Expired 1 payment(s) and 0 unfinished automatic renewal(s)");
        (await StatusAsync(waiting)).ShouldBe(PaymentStatus.Expired);
    }

    [Fact]
    public async Task Only_people_who_may_pay_can_and_only_for_a_priced_plan_through_a_gateway_that_takes_its_currency()
    {
        var free = await PlanAsync("free", 0m, 0m);
        (await Pay(new PayRequest(free, BillingCycle.Monthly, "Test"))).ErrorType.ShouldBe(ErrorType.Validation);
        (await Pay(new PayRequest(_pro, BillingCycle.Monthly, "Nowhere"))).ErrorType.ShouldBe(ErrorType.Validation);
        (await Pay(new PayRequest(Guid.NewGuid(), BillingCycle.Monthly, "Test"))).ErrorType.ShouldBe(ErrorType.Validation);

        var scripted = (await Pay(new PayRequest(_pro, BillingCycle.Monthly, "Scripted"))).GetDataOrThrow();
        scripted.IsFormPost.ShouldBeTrue("a gateway may ask for its form to be posted");

        _actor.Permissions.Remove(PermissionCodes.Subscription.Pay);
        (await Pay(new PayRequest(_pro, BillingCycle.Monthly, "Test"))).ErrorType.ShouldBe(ErrorType.Forbidden);

        var all = (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IPaymentService>().ListAllAsync(new PaymentQuery()))).GetDataOrThrow();
        all.Items.ShouldHaveSingleItem().TenantName.ShouldBe("Acme");
    }

    [Fact]
    public async Task An_automatic_renewal_charges_each_period_by_itself_and_each_charge_counts_once()
    {
        var first = await PaidRenewingAsync(_pro, BillingCycle.Annual, "evt-1");
        var renewal = (await Payments(p => p.RecurringMineAsync())).GetDataOrThrow().ShouldNotBeNull();
        renewal.ShouldSatisfyAllConditions(
            r => r.Status.ShouldBe(RecurringPaymentStatus.Active),
            r => r.Charges.ShouldBe(1),
            r => r.Amount.ShouldBe(3000m),
            r => r.FirstReference.ShouldBe(first));
        (await SubscriptionAsync()).CurrentPeriodEnd.ShouldBe(_app.Time.GetUtcNow().AddYears(1));

        _app.Time.Advance(TimeSpan.FromDays(365));
        var charge = Test(t => t.RenewalNotice(first, 3000m, "ZAR")).ToDictionary(StringComparer.Ordinal);
        (await ReceiveAsync("test", charge)).StatusCode.ShouldBe(200);
        (await ReceiveAsync("test", charge)).StatusCode.ShouldBe(200, "a repeated charge notice is acknowledged once");
        await DeliverAsync();

        (await SubscriptionAsync()).ShouldSatisfyAllConditions(
            s => s.Status.ShouldBe(SubscriptionStatus.Active),
            s => s.CurrentPeriodEnd.ShouldBe(_app.Time.GetUtcNow().AddYears(1)));
        var history = (await Payments(p => p.ListMineAsync(new PaymentQuery()))).GetDataOrThrow().Items;
        history.Count(p => p.IsRenewal && p.Status == PaymentStatus.Succeeded).ShouldBe(1);
        history.Single(p => p.Reference == first).SetsUpRenewal.ShouldBeTrue();
        (await Payments(p => p.RecurringMineAsync())).GetDataOrThrow()!.Charges.ShouldBe(2);
        _app.Sent.On(NotificationChannel.Email).Count(m => m.Subject!.StartsWith("Payment received", StringComparison.Ordinal)).ShouldBe(2, "a receipt for each charge");
    }

    [Fact]
    public async Task One_renewal_at_a_time_and_it_stops_when_the_plan_changes_the_subscription_ends_or_someone_turns_it_off()
    {
        await PaidRenewingAsync(_pro, BillingCycle.Annual, "evt-a");
        await PaidRenewingAsync(_starter, BillingCycle.Monthly, "evt-b");
        (await RenewalsAsync()).ShouldBe(["STARTER Monthly Active", "PRO Annual Cancelled: Replaced by a newer automatic renewal."], ignoreOrder: true);

        // A one-off payment for the same plan pays ahead and keeps it; for another plan it replaces it.
        await PaidAsync(new PayRequest(_starter, BillingCycle.Monthly, "Test"), "evt-c");
        (await RenewalsAsync()).ShouldContain("STARTER Monthly Active");
        await PaidAsync(new PayRequest(_pro, BillingCycle.Monthly, "Test"), "evt-d");
        (await RenewalsAsync()).ShouldContain("STARTER Monthly Cancelled: Replaced by a payment for another plan or period.");

        await PaidRenewingAsync(_pro, BillingCycle.Monthly, "evt-e");
        var id = (await Payments(p => p.RecurringMineAsync())).GetDataOrThrow()!.Id;
        (await Payments(p => p.StopRecurringAsync(id))).Message!.ShouldStartWith("Automatic renewal is off.");
        (await Payments(p => p.RecurringMineAsync())).GetDataOrThrow().ShouldBeNull();

        await PaidRenewingAsync(_pro, BillingCycle.Monthly, "evt-f");
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<ISubscriptionService>().CancelAsync(_tenant, atPeriodEnd: false))).IsSuccess.ShouldBeTrue();
        await DeliverAsync();
        (await RenewalsAsync()).ShouldAllBe(r => !r.Contains("Active", StringComparison.Ordinal), "nothing is charged after the subscription ends");
    }

    [Fact]
    public async Task The_gateway_stopping_a_renewal_tells_the_organisation_and_a_failed_stop_is_shown_until_it_works()
    {
        var first = await PaidRenewingAsync(_pro, BillingCycle.Monthly, "evt-1");
        var stopped = Test(t => t.RenewalNotice(first, 300m, "ZAR")).ToDictionary(StringComparer.Ordinal);
        stopped["outcome"] = "cancel";
        await ReceiveAsync("test", stopped);
        await DeliverAsync();
        (await RenewalsAsync()).ShouldBe(["PRO Monthly Cancelled: Stopped at Test payments."]);
        _app.Sent.On(NotificationChannel.Email).ShouldContain(m => m.To == "owner@acme.test" && m.Subject == "Automatic renewal stopped for PRO");

        // A gateway that can't be reached: the renewal stays on, with the reason, so it can be stopped another way.
        var scripted = (await Pay(new PayRequest(_pro, BillingCycle.Monthly, "Scripted", RenewAutomatically: true))).GetDataOrThrow().FormFields!["reference"];
        await ReceiveAsync("scripted", Form(scripted, "paid", "s-1", token: "SCR-TOKEN"));
        await DeliverAsync();
        var live = (await Payments(p => p.RecurringMineAsync())).GetDataOrThrow()!;
        var refused = await Payments(p => p.StopRecurringAsync(live.Id));
        refused.IsError.ShouldBeTrue();
        refused.Message!.ShouldContain("cancel it in the Scripted dashboard");
        (await Payments(p => p.RecurringMineAsync())).GetDataOrThrow()!.StopError.ShouldNotBeNull();
    }

    private async Task<string> PaidRenewingAsync(Guid plan, BillingCycle cycle, string eventId) =>
        await PaidAsync(new PayRequest(plan, cycle, "Test", RenewAutomatically: true), eventId);

    private async Task<string> PaidAsync(PayRequest request, string eventId)
    {
        var redirect = (await Pay(request)).GetDataOrThrow();
        var token = Uri.UnescapeDataString(redirect.Url.Query[(redirect.Url.Query.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        (await ReceiveAsync("test", TestForm(token, "pay", eventId))).StatusCode.ShouldBe(200);
        await DeliverAsync();
        return Test(t => t.Read(token))!.Reference;
    }

    private Task<List<string>> RenewalsAsync() =>
        _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return await db.RecurringPayments.IgnoreQueryFilters().Where(r => r.TenantId == _tenant)
                .Join(db.Plans, r => r.PlanId, p => p.Id, (r, p) => new { p.Name, r.BillingCycle, r.Status, r.CancelReason })
                .Select(r => r.Name + " " + r.BillingCycle + " " + r.Status + (r.CancelReason == null ? "" : ": " + r.CancelReason))
                .ToListAsync();
        });

    private T Test<T>(Func<ITestCheckout, T> read) => read(_app.Services.CreateScope().ServiceProvider.GetRequiredService<ITestCheckout>());

    private async Task<string> PendingReferenceAsync(string gateway)
    {
        var redirect = (await Pay(new PayRequest(_pro, BillingCycle.Monthly, gateway))).GetDataOrThrow();
        return redirect.FormFields!["reference"];
    }

    private Task<ValidationResponse<CheckoutRedirect>> Pay(PayRequest request) => Payments(p => p.StartAsync(request, Urls));

    private Task<NotificationReply> ReceiveAsync(string gateway, Dictionary<string, string> form) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IPaymentService>().ReceiveAsync(
            gateway, new InboundNotification(string.Join('&', form.Select(f => f.Key + "=" + f.Value)), form, new Dictionary<string, string>(StringComparer.Ordinal), "203.0.113.9")));

    private Task<T> Payments<T>(Func<IPaymentService, Task<T>> work) =>
        _app.InScopeAsync(_tenant, sp => work(sp.GetRequiredService<IPaymentService>()));

    private Task<PaymentStatus> StatusAsync(string reference) =>
        _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().Payments.IgnoreQueryFilters().Where(p => p.Reference == reference).Select(p => p.Status).SingleAsync());

    private Task<TenantSubscription> SubscriptionAsync() =>
        _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().TenantSubscriptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.TenantId == _tenant));

    private async Task<Guid> PlanAsync(string code, decimal monthly, decimal annual) =>
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IPlanAdministration>().CreateAsync(
            new SavePlanRequest(code, code.ToUpperInvariant(), null, "ZAR", monthly, annual, 14, 0, new Dictionary<string, long?>(StringComparer.Ordinal))))).GetDataOrThrow();

    private async Task DeliverAsync()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            await ActivatorUtilities.CreateInstance<OutboxProcessor>(_app.Services)
                .ProcessBatchAsync(_app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None);
        }
    }

    private static Dictionary<string, string> TestForm(string token, string outcome, string eventId) =>
        new(StringComparer.Ordinal) { ["token"] = token, ["outcome"] = outcome, ["event"] = eventId };

    private static Dictionary<string, string> Form(string reference, string status, string eventId, bool verified = true, string? amount = null, string? token = null) =>
        new(StringComparer.Ordinal)
        {
            ["reference"] = reference,
            ["status"] = status,
            ["event"] = eventId,
            ["verified"] = verified ? "yes" : "no",
            ["amount"] = amount ?? "300.00",
            ["token"] = token ?? string.Empty,
        };

    /// <summary>A gateway whose notifications say whatever the test posts, including that they aren't verified.</summary>
    private sealed class ScriptedGateway : IPaymentGateway
    {
        public string Code => "Scripted";

        public string Name => "Scripted";

        public IReadOnlyList<string> Methods { get; } = ["Card"];

        public IReadOnlyList<string> Currencies { get; } = ["ZAR"];

        public bool SupportsRecurring => true;

        public Task<GatewayResult> CancelRecurringAsync(string token, CancellationToken cancellationToken) => Task.FromResult(GatewayResult.Failed("Unreachable."));

        public Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CheckoutStart.Go(new CheckoutRedirect(new Uri("https://scripted.example.com/pay"), new Dictionary<string, string>(StringComparer.Ordinal) { ["reference"] = request.Reference }), "CHK-" + request.Reference));

        public Task<GatewayNotification> ReadNotificationAsync(InboundNotification notification, CancellationToken cancellationToken)
        {
            var f = notification.Form;
            if (f["verified"] != "yes")
            {
                return Task.FromResult(GatewayNotification.Rejected("Bad signature."));
            }

            var kind = f["status"] == "paid" ? GatewayNotificationKind.Paid : GatewayNotificationKind.Failed;
            var byCheckout = f.GetValueOrDefault("checkout") is { Length: > 0 } checkout ? checkout : null;
            return Task.FromResult(new GatewayNotification(
                true, f["event"], byCheckout is null ? f["reference"] : null, "SCR-" + f["event"], kind, decimal.Parse(f["amount"], CultureInfo.InvariantCulture),
                kind == GatewayNotificationKind.Failed ? "Card declined." : null, f.GetValueOrDefault("token") is { Length: > 0 } t ? t : null, byCheckout));
        }
    }
}
