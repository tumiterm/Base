namespace Base.Application.Payments;

/// <summary>
/// A payment provider (Payfast, Yoco...). It starts a checkout on its own hosted page, so card details never touch
/// this application, and reads the notifications it sends back. Implemented in Infrastructure; registered only when
/// configured under <c>Payments:Gateways:{Code}</c>.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Stable code, used in notification URLs and stored on each payment (<c>Payfast</c>).</summary>
    string Code { get; }

    string Name { get; }

    /// <summary>How people can pay on its page, for the chooser ("Card", "Instant EFT", "QR code", "Apple Pay"...).</summary>
    IReadOnlyList<string> Methods { get; }

    /// <summary>ISO currency codes it accepts.</summary>
    IReadOnlyList<string> Currencies { get; }

    Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>Verifies a notification (signature, source, and the gateway's own confirmation where it has one) and reads it.</summary>
    Task<GatewayNotification> ReadNotificationAsync(InboundNotification notification, CancellationToken cancellationToken);

    /// <summary>Whether it can charge each billing cycle by itself (<see cref="CheckoutRequest.RenewEvery"/>).</summary>
    bool SupportsRecurring => false;

    /// <summary>Stops an automatic renewal at the gateway, so nothing more is charged.</summary>
    Task<GatewayResult> CancelRecurringAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(GatewayResult.Failed($"{Name} has no automatic renewals to stop."));

    /// <summary>Whether <see cref="CheckAsync"/> can ask the gateway how a payment went.</summary>
    bool SupportsStatusCheck => false;

    /// <summary>
    /// Asks the gateway, server to server with our credentials, how the payment with this reference (and the gateway's
    /// own <paramref name="gatewayReference"/>) went. As trustworthy as a verified notification, and how payments are
    /// confirmed when notifications can't reach the app (on a developer's machine) or are late.
    /// </summary>
    Task<GatewayNotification> CheckAsync(string reference, string? gatewayReference, CancellationToken cancellationToken) =>
        Task.FromResult(GatewayNotification.Rejected($"{Name} can't be asked how a payment went."));

    /// <summary>The smallest amount it takes in one payment (Payfast R5.00, Yoco R2.00).</summary>
    decimal MinimumCharge => 0m;

    /// <summary>The largest amount it takes in one payment, when the merchant account has a limit; null for none known.</summary>
    decimal? MaximumCharge => null;

    /// <summary>
    /// The largest amount an automatic renewal may charge each cycle (Payfast accounts limit subscription amounts);
    /// null for none known.
    /// </summary>
    decimal? MaximumRecurringCharge => null;

    /// <summary>Whether <see cref="UpdateRecurringAsync"/> can change what an automatic renewal charges.</summary>
    bool SupportsRecurringUpdate => false;

    /// <summary>
    /// Changes what an automatic renewal charges from its next charge on: the amount and how often (after a change of
    /// plan or billing cycle). The next charge date stays as it is.
    /// </summary>
    Task<GatewayResult> UpdateRecurringAsync(string token, decimal amount, Base.Domain.Subscriptions.BillingCycle cycle, CancellationToken cancellationToken) =>
        Task.FromResult(GatewayResult.Failed($"{Name} can't change an automatic renewal."));

    /// <summary>Whether <see cref="UpdateCardUrl"/> gives a page where the payer changes the card a renewal charges.</summary>
    bool SupportsCardUpdate => false;

    /// <summary>The gateway's page where the payer changes the card an automatic renewal charges, if it has one.</summary>
    Uri? UpdateCardUrl(string token, Uri returnUrl) => null;
}

public sealed record GatewayResult(bool Succeeded, string? Error)
{
    public static GatewayResult Ok() => new(true, null);

    public static GatewayResult Failed(string error) => new(false, error);
}

/// <param name="Reference">Our payment reference; the gateway sends it back in its notifications.</param>
/// <param name="ReturnUrl">Where the person lands after paying.</param>
/// <param name="CancelUrl">Where the person lands after leaving without paying.</param>
/// <param name="NotifyUrl">Where the gateway posts its notification for this payment.</param>
/// <param name="RenewEvery">Set to have the gateway charge the same amount every cycle until stopped.</param>
public sealed record CheckoutRequest(
    string Reference,
    decimal Amount,
    string Currency,
    string Description,
    string? CustomerEmail,
    string? CustomerName,
    Uri ReturnUrl,
    Uri CancelUrl,
    Uri NotifyUrl,
    Base.Domain.Subscriptions.BillingCycle? RenewEvery = null);

/// <summary>
/// Where to send the person: a plain redirect, or (with <see cref="FormFields"/>) a form posted to the gateway, which
/// the browser submits by itself.
/// </summary>
public sealed record CheckoutRedirect(Uri Url, IReadOnlyDictionary<string, string>? FormFields = null)
{
    public bool IsFormPost => FormFields is not null;

    /// <summary>Our payment's reference, set once the payment is recorded (for forwarding links).</summary>
    public string? PaymentReference { get; init; }
}

/// <param name="GatewayReference">The gateway's own id for the checkout, when it gives one up front.</param>
public sealed record CheckoutStart(CheckoutRedirect? Redirect, string? GatewayReference, string? Error)
{
    public static CheckoutStart Go(CheckoutRedirect redirect, string? gatewayReference = null) => new(redirect, gatewayReference, null);

    public static CheckoutStart Failed(string error) => new(null, null, error);
}

/// <summary>A notification as it arrived: the raw body (for signatures), parsed form fields, headers and the sender's address.</summary>
public sealed record InboundNotification(
    string Body,
    IReadOnlyDictionary<string, string> Form,
    IReadOnlyDictionary<string, string> Headers,
    string? RemoteIp);

public enum GatewayNotificationKind
{
    /// <summary>Verified, but nothing we act on (a refund notice, a test ping...).</summary>
    Other = 0,

    Paid = 1,

    Failed = 2,

    Cancelled = 3,
}

/// <param name="Verified">False when it isn't provably from the gateway: it is then recorded and ignored.</param>
/// <param name="EventId">The gateway's id for this notification, so a repeat is recognised.</param>
/// <param name="Reference">Our payment reference, read from the notification.</param>
/// <param name="Amount">The amount the gateway says was paid, checked against the payment.</param>
/// <param name="RecurringToken">The gateway's id for the automatic renewal the notification is about, if any.</param>
/// <param name="CheckoutId">The gateway's id for the checkout, given when it was started: finds the payment when the
/// notification doesn't carry our reference.</param>
public sealed record GatewayNotification(
    bool Verified,
    string? EventId,
    string? Reference,
    string? GatewayReference,
    GatewayNotificationKind Kind,
    decimal? Amount,
    string? Detail,
    string? RecurringToken = null,
    string? CheckoutId = null)
{
    public static GatewayNotification Rejected(string detail) => new(false, null, null, null, GatewayNotificationKind.Other, null, detail);
}

/// <summary>The built-in Test gateway's checkout page, for development and tests: what the person is asked to pay.</summary>
public interface ITestCheckout
{
    /// <summary>The checkout a token describes, or null when the token isn't one of ours (or the Test gateway is off).</summary>
    TestCheckoutView? Read(string token);

    /// <summary>The notification the Test gateway would send for the next charge of an automatic renewal.</summary>
    IReadOnlyDictionary<string, string> RenewalNotice(string firstReference, decimal amount, string currency);
}

/// <param name="Recurring">The checkout also sets up an automatic renewal.</param>
public sealed record TestCheckoutView(string Reference, decimal Amount, string Currency, string Description, Uri ReturnUrl, Uri CancelUrl, bool Recurring = false);

/// <summary>
/// Origins the browser is sent to, by form post or a redirect after one, for the configured gateways. The web layer
/// adds them to the CSP's <c>form-action</c>.
/// </summary>
public sealed record PaymentCheckoutOrigins(IReadOnlyList<string> Origins)
{
    public static PaymentCheckoutOrigins None { get; } = new([]);
}
