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
}

/// <param name="Reference">Our payment reference; the gateway sends it back in its notifications.</param>
/// <param name="ReturnUrl">Where the person lands after paying.</param>
/// <param name="CancelUrl">Where the person lands after leaving without paying.</param>
/// <param name="NotifyUrl">Where the gateway posts its notification for this payment.</param>
public sealed record CheckoutRequest(
    string Reference,
    decimal Amount,
    string Currency,
    string Description,
    string? CustomerEmail,
    string? CustomerName,
    Uri ReturnUrl,
    Uri CancelUrl,
    Uri NotifyUrl);

/// <summary>
/// Where to send the person: a plain redirect, or (with <see cref="FormFields"/>) a form posted to the gateway, which
/// the browser submits by itself.
/// </summary>
public sealed record CheckoutRedirect(Uri Url, IReadOnlyDictionary<string, string>? FormFields = null)
{
    public bool IsFormPost => FormFields is not null;
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
public sealed record GatewayNotification(
    bool Verified,
    string? EventId,
    string? Reference,
    string? GatewayReference,
    GatewayNotificationKind Kind,
    decimal? Amount,
    string? Detail)
{
    public static GatewayNotification Rejected(string detail) => new(false, null, null, null, GatewayNotificationKind.Other, null, detail);
}

/// <summary>The built-in Test gateway's checkout page, for development and tests: what the person is asked to pay.</summary>
public interface ITestCheckout
{
    /// <summary>The checkout a token describes, or null when the token isn't one of ours (or the Test gateway is off).</summary>
    TestCheckoutView? Read(string token);
}

public sealed record TestCheckoutView(string Reference, decimal Amount, string Currency, string Description, Uri ReturnUrl, Uri CancelUrl);

/// <summary>
/// Origins the browser is sent to, by form post or a redirect after one, for the configured gateways. The web layer
/// adds them to the CSP's <c>form-action</c>.
/// </summary>
public sealed record PaymentCheckoutOrigins(IReadOnlyList<string> Origins)
{
    public static PaymentCheckoutOrigins None { get; } = new([]);
}
