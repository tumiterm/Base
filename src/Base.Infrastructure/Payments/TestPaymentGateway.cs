using System.Security.Cryptography;
using System.Text.Json;
using Base.Application.Payments;
using Microsoft.AspNetCore.DataProtection;

namespace Base.Infrastructure.Payments;

public sealed class TestGatewayOptions
{
    public const string SectionName = "Payments:Gateways:Test";

    /// <summary>Off by default; on in Development so the whole pay flow can be tried without a provider account.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// A pretend gateway: its checkout is a page in this application (<c>/payments/test</c>) with Pay, Decline and Cancel,
/// which reports back through the same notification path as a real gateway. The checkout is a data-protected token,
/// so a notification can't be forged. Never enable it in production.
/// </summary>
internal sealed class TestPaymentGateway(IDataProtectionProvider protection) : IPaymentGateway, ITestCheckout
{
    public const string GatewayCode = "Test";
    public const string TokenField = "token";
    public const string OutcomeField = "outcome";
    public const string EventField = "event";
    public const string RenewalField = "renewal";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = protection.CreateProtector("Base.Payments.TestGateway");

    public string Code => GatewayCode;

    public string Name => "Test payments";

    public IReadOnlyList<string> Methods { get; } = ["Pretend card", "No money moves"];

    public IReadOnlyList<string> Currencies { get; } = ["ZAR", "USD", "EUR", "GBP"];

    public bool SupportsRecurring => true;

    public Task<GatewayResult> CancelRecurringAsync(string token, CancellationToken cancellationToken) => Task.FromResult(GatewayResult.Ok());

    public Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = new TestCheckoutView(request.Reference, request.Amount, request.Currency, request.Description, request.ReturnUrl, request.CancelUrl, request.RenewEvery is not null);
        var token = _protector.Protect(JsonSerializer.Serialize(view, Json));
        var page = new Uri(request.ReturnUrl, "/payments/test?" + TokenField + "=" + Uri.EscapeDataString(token));
        return Task.FromResult(CheckoutStart.Go(new CheckoutRedirect(page), "TEST-" + request.Reference));
    }

    public Task<GatewayNotification> ReadNotificationAsync(InboundNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!notification.Form.TryGetValue(TokenField, out var token) || Read(token) is not { } checkout)
        {
            return Task.FromResult(GatewayNotification.Rejected("The test checkout token is missing or not ours."));
        }

        var kind = notification.Form.GetValueOrDefault(OutcomeField) switch
        {
            "pay" => GatewayNotificationKind.Paid,
            "decline" => GatewayNotificationKind.Failed,
            "cancel" => GatewayNotificationKind.Cancelled,
            _ => GatewayNotificationKind.Other,
        };
        var eventId = notification.Form.GetValueOrDefault(EventField);
        var renewal = notification.Form.ContainsKey(RenewalField);
        return Task.FromResult(new GatewayNotification(
            true,
            eventId,
            checkout.Reference,
            renewal ? "TEST-" + eventId : "TEST-" + checkout.Reference,
            kind,
            checkout.Amount,
            kind == GatewayNotificationKind.Failed ? "Declined on the test checkout page." : null,
            checkout.Recurring ? "TEST-TOKEN-" + checkout.Reference : null));
    }

    public IReadOnlyDictionary<string, string> RenewalNotice(string firstReference, decimal amount, string currency)
    {
        var view = new TestCheckoutView(firstReference, amount, currency, "Renewal", new Uri("https://example.invalid/"), new Uri("https://example.invalid/"), Recurring: true);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TokenField] = _protector.Protect(JsonSerializer.Serialize(view, Json)),
            [OutcomeField] = "pay",
            [EventField] = Guid.NewGuid().ToString("N"),
            [RenewalField] = "1",
        };
    }

    public TestCheckoutView? Read(string token)
    {
        try
        {
            return JsonSerializer.Deserialize<TestCheckoutView>(_protector.Unprotect(token), Json);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
