using Base.Application.Payments;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Payments;

/// <summary>Confirming a payment by asking the gateway, where its notification never arrives (a developer's machine).</summary>
public sealed partial class PaymentTests
{
    [Fact]
    public async Task Coming_back_asks_the_gateway_and_its_answer_applies_once_without_any_notification()
    {
        var asked = _app.Services.GetRequiredService<AskedGateway>();
        var reference = (await Pay(new PayRequest(_pro, BillingCycle.Monthly, "Asked"))).GetDataOrThrow().PaymentReference!;

        (await Payments(p => p.ReturnedAsync(reference, cancelled: false))).GetDataOrThrow().Status.ShouldBe(PaymentStatus.Pending, "the gateway says it's still waiting");
        asked.Paid.Add(reference);
        var back = (await Payments(p => p.ReturnedAsync(reference, cancelled: false))).GetDataOrThrow();
        back.Status.ShouldBe(PaymentStatus.Succeeded);
        await DeliverAsync();
        (await SubscriptionAsync()).ShouldSatisfyAllConditions(s => s.PlanId.ShouldBe(_pro), s => s.Status.ShouldBe(SubscriptionStatus.Active));

        (await Payments(p => p.ReturnedAsync(reference, cancelled: false))).GetDataOrThrow().Applied.ShouldBeTrue();
        asked.Checks.ShouldBe(2, "a payment that is settled isn't asked about again");

        asked.Paid.Clear();
        asked.Amount = 1m;
        var cheat = (await Pay(new PayRequest(_pro, BillingCycle.Monthly, "Asked"))).GetDataOrThrow().PaymentReference!;
        asked.Paid.Add(cheat);
        (await Payments(p => p.ReturnedAsync(cheat, cancelled: false))).GetDataOrThrow().Status.ShouldBe(PaymentStatus.Pending, "an answer for the wrong amount is rejected");
    }

    /// <summary>A gateway whose notifications never come, but which answers when asked.</summary>
    private sealed class AskedGateway : IPaymentGateway
    {
        public HashSet<string> Paid { get; } = new(StringComparer.Ordinal);

        public decimal? Amount { get; set; }

        public int Checks { get; private set; }

        public string Code => "Asked";

        public string Name => "Asked";

        public IReadOnlyList<string> Methods { get; } = ["Card"];

        public IReadOnlyList<string> Currencies { get; } = ["ZAR"];

        public bool SupportsStatusCheck => true;

        public Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CheckoutStart.Go(new CheckoutRedirect(new Uri("https://asked.example.com/pay/" + request.Reference)), "ASK-" + request.Reference));

        public Task<GatewayNotification> ReadNotificationAsync(InboundNotification notification, CancellationToken cancellationToken) =>
            Task.FromResult(GatewayNotification.Rejected("Never sends any."));

        public Task<GatewayNotification> CheckAsync(string reference, string? gatewayReference, CancellationToken cancellationToken)
        {
            Checks++;
            gatewayReference.ShouldBe("ASK-" + reference);
            return Task.FromResult(Paid.Contains(reference)
                ? new GatewayNotification(true, null, reference, "ASK-PAY-" + reference, GatewayNotificationKind.Paid, Amount ?? 300m, null, CheckoutId: gatewayReference)
                : new GatewayNotification(true, null, reference, null, GatewayNotificationKind.Other, null, "Waiting"));
        }
    }
}
