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
        result.ShouldBe("Expired 1 payment(s)");
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

    private static Dictionary<string, string> Form(string reference, string status, string eventId, bool verified = true, string? amount = null) =>
        new(StringComparer.Ordinal)
        {
            ["reference"] = reference,
            ["status"] = status,
            ["event"] = eventId,
            ["verified"] = verified ? "yes" : "no",
            ["amount"] = amount ?? "300.00",
        };

    /// <summary>A gateway whose notifications say whatever the test posts, including that they aren't verified.</summary>
    private sealed class ScriptedGateway : IPaymentGateway
    {
        public string Code => "Scripted";

        public string Name => "Scripted";

        public IReadOnlyList<string> Methods { get; } = ["Card"];

        public IReadOnlyList<string> Currencies { get; } = ["ZAR"];

        public Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CheckoutStart.Go(new CheckoutRedirect(new Uri("https://scripted.example.com/pay"), new Dictionary<string, string>(StringComparer.Ordinal) { ["reference"] = request.Reference })));

        public Task<GatewayNotification> ReadNotificationAsync(InboundNotification notification, CancellationToken cancellationToken)
        {
            var f = notification.Form;
            if (f["verified"] != "yes")
            {
                return Task.FromResult(GatewayNotification.Rejected("Bad signature."));
            }

            var kind = f["status"] == "paid" ? GatewayNotificationKind.Paid : GatewayNotificationKind.Failed;
            return Task.FromResult(new GatewayNotification(true, f["event"], f["reference"], "SCR-" + f["event"], kind, decimal.Parse(f["amount"], CultureInfo.InvariantCulture), kind == GatewayNotificationKind.Failed ? "Card declined." : null));
        }
    }
}
