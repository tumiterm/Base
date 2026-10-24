using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Operations;
using Base.Application.Payments;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Application.Users;
using Base.Domain.Notifications;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Payments;

/// <summary>Changing plan part-way through a period: upgrades, downgrades, limits, the operator, and automatic renewals.</summary>
public sealed partial class PaymentTests
{
    [Fact]
    public async Task An_upgrade_applies_now_for_the_difference_over_the_rest_of_the_period_and_the_renewal_follows()
    {
        await PaidRenewingAsync(_starter, BillingCycle.Monthly, "u-1");
        var paidUpTo = (await SubscriptionAsync()).CurrentPeriodEnd;
        _app.Time.Advance(TimeSpan.FromDays(10));

        var preview = (await Subscriptions(s => s.PreviewMineAsync(_pro, BillingCycle.Monthly))).GetDataOrThrow();
        preview.Kind.ShouldBe(PlanChangeKind.Upgrade);
        preview.AmountDueNow.ShouldBe(PlanChangeRules.ProRata(200m, await SubscriptionAsync(), _app.Time.GetUtcNow()));
        preview.AmountDueNow.ShouldBeInRange(100m, 199.99m);

        var redirect = (await Pay(new PayRequest(_pro, BillingCycle.Monthly, "Test", RenewAutomatically: true))).GetDataOrThrow();
        var token = Uri.UnescapeDataString(redirect.Url.Query[(redirect.Url.Query.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        Test(t => t.Read(token))!.Amount.ShouldBe(preview.AmountDueNow, "only the difference, once");
        await ReceiveAsync("test", TestForm(token, "pay", "u-2"));
        await DeliverAsync();

        (await SubscriptionAsync()).ShouldSatisfyAllConditions(
            s => s.PlanId.ShouldBe(_pro),
            s => s.CurrentPeriodEnd.ShouldBe(paidUpTo, "the paid period stays as it was"));
        var renewal = (await Payments(p => p.RecurringMineAsync())).GetDataOrThrow()!;
        renewal.ShouldSatisfyAllConditions(r => r.PlanName.ShouldBe("PRO"), r => r.Amount.ShouldBe(300m), r => r.Status.ShouldBe(RecurringPaymentStatus.Active));
        (await RenewalsAsync()).ShouldBe(["PRO Monthly Active"], "the same renewal, now charging for Pro");
        (await Payments(p => p.ListMineAsync(new PaymentQuery()))).GetDataOrThrow().Items.Single(p => p.Amount == preview.AmountDueNow).IsUpgrade.ShouldBeTrue();
    }

    [Fact]
    public async Task A_downgrade_waits_for_the_period_end_needs_the_organisation_within_its_limits_and_keeps_it_there()
    {
        var solo = await PlanAsync("solo", 50m, 500m, users: 1);
        var team = await PlanAsync("team", 150m, 1500m, users: 3);
        await PaidAsync(new PayRequest(team, BillingCycle.Monthly, "Test"), "d-1");
        var paidUpTo = (await SubscriptionAsync()).CurrentPeriodEnd;
        var ann = await InviteAsync("ann@acme.test");
        var ben = await InviteAsync("ben@acme.test");

        var refused = await Subscriptions(s => s.ChangeMineAsync(solo, BillingCycle.Monthly));
        refused.ShouldSatisfyAllConditions(r => r.IsError.ShouldBeTrue(), r => r.Message!.ShouldContain("3 users; the plan allows 1. Lock or remove 2 first"));
        (await Pay(new PayRequest(solo, BillingCycle.Monthly, "Test"))).Message!.ShouldContain("allows less than you use", customMessage: "nor can it be paid for");

        (await Users(u => u.LockAsync(ann))).IsSuccess.ShouldBeTrue();
        (await Users(u => u.LockAsync(ben))).IsSuccess.ShouldBeTrue();
        var scheduled = await Subscriptions(s => s.ChangeMineAsync(solo, BillingCycle.Monthly));
        scheduled.Message!.ShouldStartWith("You'll move to SOLO on");
        (await SubscriptionAsync()).ShouldSatisfyAllConditions(
            s => s.PlanId.ShouldBe(team, "nothing changes until the paid period ends"),
            s => s.ScheduledPlanId.ShouldBe(solo),
            s => s.ScheduledChangeAt.ShouldBe(paidUpTo));

        // Meanwhile the smaller plan's limits apply, so the change can still go ahead.
        (await Users(u => u.UnlockAsync(ann))).Message!.ShouldStartWith("Your plan changes to SOLO on");
        (await Users(u => u.InviteAsync(new InviteUserRequest("cy@acme.test", "Cy", [RoleNames.User])))).Message!.ShouldStartWith("Your plan changes to SOLO on");

        (await Subscriptions(s => s.KeepMyPlanAsync())).Message.ShouldBe("The plan stays as it is.");
        (await Users(u => u.UnlockAsync(ann))).IsSuccess.ShouldBeTrue("back on Team's three seats");
        (await Users(u => u.LockAsync(ann))).IsSuccess.ShouldBeTrue();

        (await Subscriptions(s => s.ChangeMineAsync(solo, BillingCycle.Monthly))).IsSuccess.ShouldBeTrue();
        _app.Time.Advance(paidUpTo - _app.Time.GetUtcNow() + TimeSpan.FromMinutes(1));
        await _app.InScopeAsync(null, async sp =>
        {
            // The scheduler runs jobs in system scope.
            using var system = sp.GetRequiredService<ITenantContext>().BeginSystemScope();
            return await sp.GetServices<IScheduledJob>().Single(j => j.Code == "subscription-lifecycle").RunAsync(CancellationToken.None);
        });
        (await SubscriptionAsync()).ShouldSatisfyAllConditions(
            s => s.PlanId.ShouldBe(solo),
            s => s.HasScheduledChange.ShouldBeFalse(),
            s => s.Status.ShouldBe(SubscriptionStatus.PastDue, "the next period, on Solo, isn't paid yet"));
    }

    [Fact]
    public async Task The_operator_gives_a_reason_tells_the_organisation_and_overrides_limits_only_on_purpose()
    {
        var solo = await PlanAsync("solo", 50m, 500m, users: 1);
        await PaidAsync(new PayRequest(_pro, BillingCycle.Monthly, "Test"), "o-1");
        await InviteAsync("ann@acme.test");
        await DeliverAsync();
        _app.Sent.Clear();

        (await Operator(s => s.ChangePlanAsync(_tenant, new ChangePlanRequest(solo, BillingCycle.Monthly))))
            .Errors.ShouldHaveSingleItem().Field.ShouldBe(nameof(ChangePlanRequest.Reason));
        var over = await Operator(s => s.ChangePlanAsync(_tenant, new ChangePlanRequest(solo, BillingCycle.Monthly, Reason: "Asked to save costs")));
        over.ShouldSatisfyAllConditions(r => r.Errors.ShouldContain(e => e.Code == "plan.over-limits"), r => r.Message!.ShouldContain("2 users; the plan allows 1"));
        (await SubscriptionAsync()).PlanId.ShouldBe(_pro);

        (await Operator(s => s.ChangePlanAsync(_tenant, new ChangePlanRequest(solo, BillingCycle.Monthly, Reason: "Asked to save costs", AllowOverLimits: true)))).IsSuccess.ShouldBeTrue();
        await DeliverAsync();
        (await SubscriptionAsync()).PlanId.ShouldBe(solo);
        var told = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem();
        told.ShouldSatisfyAllConditions(
            m => m.To.ShouldBe("owner@acme.test"),
            m => m.Subject!.ShouldEndWith("plan changes to SOLO"),
            m => m.Body!.ShouldContain("from PRO to"),
            m => m.Body!.ShouldContain("today: Asked to save costs"));

        // At the end of the period instead, and withdrawn again.
        _app.Sent.Clear();
        var later = await Operator(s => s.ChangePlanAsync(_tenant, new ChangePlanRequest(_pro, BillingCycle.Monthly, PlanChangeTiming.AtPeriodEnd, "Back to Pro next month")));
        later.Message!.ShouldStartWith("Moves to PRO on");
        await DeliverAsync();
        _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().Body!.ShouldContain("on " + (await SubscriptionAsync()).CurrentPeriodEnd.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        (await Operator(s => s.CancelScheduledChangeAsync(_tenant))).IsSuccess.ShouldBeTrue();
        (await SubscriptionAsync()).HasScheduledChange.ShouldBeFalse();
    }

    [Fact]
    public async Task A_renewal_whose_gateway_cannot_follow_a_change_of_plan_is_stopped_and_the_organisation_told()
    {
        var reference = (await Pay(new PayRequest(_pro, BillingCycle.Monthly, "Rigid", RenewAutomatically: true))).GetDataOrThrow().FormFields!["reference"];
        await ReceiveAsync("rigid", Form(reference, "paid", "g-1", token: "RIG-TOKEN"));
        await DeliverAsync();
        (await RenewalsAsync()).ShouldBe(["PRO Monthly Active"]);
        _app.Sent.Clear();

        (await Subscriptions(s => s.ChangeMineAsync(_starter, BillingCycle.Monthly))).IsSuccess.ShouldBeTrue();
        await DeliverAsync();

        (await RenewalsAsync()).ShouldBe(["PRO Monthly Cancelled: The plan changed and the renewal couldn't follow it."], "never charged the wrong amount");
        var told = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem();
        told.Subject.ShouldBe("Automatic renewal stopped for PRO");
        told.Body!.ShouldContain("Rigid can't change what it charges");
    }

    private Task<T> Subscriptions<T>(Func<ISubscriptionService, Task<T>> work) =>
        _app.InScopeAsync(_tenant, sp => work(sp.GetRequiredService<ISubscriptionService>()));

    private Task<ValidationResponse> Operator(Func<ISubscriptionService, Task<ValidationResponse>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<ISubscriptionService>()));

    private Task<T> Users<T>(Func<IUserAdministration, Task<T>> work) =>
        _app.InScopeAsync(_tenant, sp => work(sp.GetRequiredService<IUserAdministration>()));

    private async Task<Guid> InviteAsync(string email) =>
        (await Users(u => u.InviteAsync(new InviteUserRequest(email, email[..email.IndexOf('@', StringComparison.Ordinal)], [RoleNames.User])))).GetDataOrThrow();

    private async Task<Guid> PlanAsync(string code, decimal monthly, decimal annual, long users) =>
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IPlanAdministration>().CreateAsync(
            new SavePlanRequest(code, code.ToUpperInvariant(), null, "ZAR", monthly, annual, 14, 0, new Dictionary<string, long?>(StringComparer.Ordinal) { [LimitCodes.Users] = users })))).GetDataOrThrow();

    /// <summary>A gateway with automatic renewals it can stop but not change, like many card schemes.</summary>
    private sealed class RigidGateway : IPaymentGateway
    {
        private readonly ScriptedGateway _reader = new();

        public string Code => "Rigid";

        public string Name => "Rigid";

        public IReadOnlyList<string> Methods { get; } = ["Card"];

        public IReadOnlyList<string> Currencies { get; } = ["ZAR"];

        public bool SupportsRecurring => true;

        public Task<GatewayResult> CancelRecurringAsync(string token, CancellationToken cancellationToken) => Task.FromResult(GatewayResult.Ok());

        public Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CheckoutStart.Go(new CheckoutRedirect(new Uri("https://rigid.example.com/pay"), new Dictionary<string, string>(StringComparer.Ordinal) { ["reference"] = request.Reference })));

        public Task<GatewayNotification> ReadNotificationAsync(InboundNotification notification, CancellationToken cancellationToken) =>
            _reader.ReadNotificationAsync(notification, cancellationToken);
    }
}
