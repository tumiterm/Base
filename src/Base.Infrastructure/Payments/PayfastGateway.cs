using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Base.Application.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Payments;

public sealed class PayfastOptions
{
    public const string SectionName = "Payments:Gateways:Payfast";

    public bool Enabled { get; set; }

    /// <summary>From the Payfast dashboard (Settings). Sandbox: 10000100.</summary>
    public string MerchantId { get; set; } = string.Empty;

    public string MerchantKey { get; set; } = string.Empty;

    /// <summary>The "Salt Passphrase" set in the Payfast dashboard. Strongly recommended; required for subscriptions.</summary>
    public string? Passphrase { get; set; }

    /// <summary>Use sandbox.payfast.co.za instead of www.payfast.co.za.</summary>
    public bool Sandbox { get; set; }

    /// <summary>Limit the checkout to one method (Payfast's code, e.g. <c>cc</c>, <c>ef</c>, <c>ap</c>); empty shows all.</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>
    /// What the chooser shows; empty shows <see cref="DefaultMethods"/>. List what is switched on in the Payfast account
    /// (add Apple Pay, Samsung Pay... once they are). Set in configuration, so it replaces the default rather than adding to it.
    /// </summary>
    public IList<string> Methods { get; set; } = [];

    public static IReadOnlyList<string> DefaultMethods { get; } = ["Card", "Instant EFT", "QR code"];

    /// <summary>
    /// The most one payment may be, if the Payfast account limits it (Payfast refuses more with its own error page, so
    /// the app checks first). Empty for no limit.
    /// </summary>
    public decimal? MaxAmount { get; set; }

    /// <summary>
    /// The most an automatic renewal (Payfast subscription) may charge each cycle. Payfast accounts limit subscription
    /// amounts (refused with "The subscription amount is outside the limits set by the merchant or PayFast"): set this
    /// to the account's limit, so larger plans are paid once instead. Empty for no limit.
    /// </summary>
    public decimal? MaxRecurringAmount { get; set; }

    /// <summary>Accept notifications only from Payfast's addresses (resolved from its host names). Keep on in production.</summary>
    public bool VerifySourceAddress { get; set; } = true;

    /// <summary>Extra addresses notifications may come from (Payfast's published IPs), as IPs or CIDR ranges.</summary>
    public IList<string> AllowedSourceAddresses { get; set; } = [];

    public string Host => Sandbox ? "sandbox.payfast.co.za" : "www.payfast.co.za";
}

/// <summary>
/// Payfast's signature rules, as its documentation and PHP samples define them: PHP <c>urlencode</c> (upper-case hex,
/// spaces as '+'), fields in the documented order for the checkout and in the posted order for notifications, the
/// passphrase appended last, and an MD5 in lower case.
/// </summary>
public static class PayfastSignature
{
    /// <summary>PHP's <c>urlencode</c>: letters, digits and <c>-_.</c> stay; spaces become '+'; the rest is %XX in UTF-8.</summary>
    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                builder.Append(c);
            }
            else if (c == ' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>The checkout form's signature: non-blank fields in order, trimmed and encoded, then the passphrase.</summary>
    public static string ForCheckout(IEnumerable<KeyValuePair<string, string>> fields, string? passphrase)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var text = string.Join('&', fields.Where(f => f.Value.Length > 0).Select(f => f.Key + "=" + Encode(f.Value.Trim())));
        if (!string.IsNullOrEmpty(passphrase))
        {
            text += "&passphrase=" + Encode(passphrase.Trim());
        }

        return Md5(text);
    }

    /// <summary>
    /// The parameter string of a notification: every posted field in the order it arrived, up to (not including)
    /// <c>signature</c>, re-encoded. Payfast's server confirmation takes this same string.
    /// </summary>
    public static string NotificationParameters(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var pairs = new List<string>();
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(equals < 0 ? part : part[..equals]);
            if (string.Equals(key, "signature", StringComparison.Ordinal))
            {
                break;
            }

            pairs.Add(key + "=" + Encode(equals < 0 ? string.Empty : Decode(part[(equals + 1)..])));
        }

        return string.Join('&', pairs);
    }

    public static string ForNotification(string parameters, string? passphrase) =>
        Md5(string.IsNullOrEmpty(passphrase) ? parameters : parameters + "&passphrase=" + Encode(passphrase));

    /// <summary>
    /// The API's signature: headers and body values with the passphrase, sorted alphabetically by name (unlike the
    /// checkout, which keeps the documented order), encoded and hashed.
    /// </summary>
    public static string ForApi(IReadOnlyDictionary<string, string> values, string? passphrase)
    {
        ArgumentNullException.ThrowIfNull(values);
        var all = new SortedDictionary<string, string>(values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(passphrase))
        {
            all["passphrase"] = passphrase;
        }

        return Md5(string.Join('&', all.Select(v => Encode(v.Key) + "=" + Encode(v.Value))));
    }

    private static string Decode(string value) => WebUtility.UrlDecode(value);

    private static string Md5(string text) =>
#pragma warning disable CA5351 // Payfast's protocol mandates MD5; it is an integrity check with a shared secret, not a password hash.
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
#pragma warning restore CA5351
}

/// <summary>
/// Payfast's hosted checkout (card, Instant EFT, QR codes, Apple Pay, Samsung Pay... whatever the merchant account has
/// switched on): a signed form the browser posts to Payfast, and the ITN Payfast posts back, checked four ways as
/// Payfast requires (signature, source, amount, server confirmation).
/// </summary>
internal sealed partial class PayfastGateway(IOptions<PayfastOptions> options, IHttpClientFactory httpClients, TimeProvider time, ILogger<PayfastGateway> logger) : IPaymentGateway
{
    public const string GatewayCode = "Payfast";
    public const string HttpClientName = "payfast";

    /// <summary>Payfast won't process less than this.</summary>
    public const decimal MinimumAmount = 5m;

    /// <summary>Where Payfast says its notifications come from (sandbox included).</summary>
    public static readonly IReadOnlyList<string> NotificationHosts = ["www.payfast.co.za", "sandbox.payfast.co.za", "w1w.payfast.co.za", "w2w.payfast.co.za"];

    private readonly PayfastOptions _options = options.Value;

    public string Code => GatewayCode;

    public string Name => _options.Sandbox ? "Payfast (sandbox)" : "Payfast";

    public IReadOnlyList<string> Methods => _options.Methods.Count > 0 ? [.. _options.Methods] : PayfastOptions.DefaultMethods;

    public IReadOnlyList<string> Currencies { get; } = ["ZAR"];

    /// <summary>Payfast Subscriptions: Payfast charges the card every month or year until stopped.</summary>
    public bool SupportsRecurring => true;

    public bool SupportsCardUpdate => true;

    public Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount < MinimumAmount)
        {
            return Task.FromResult(CheckoutStart.Failed("Payfast can't take payments under R5.00."));
        }

        if (request.RenewEvery is not null && string.IsNullOrEmpty(_options.Passphrase))
        {
            return Task.FromResult(CheckoutStart.Failed("Automatic renewal through Payfast needs the merchant passphrase to be configured."));
        }

        var fields = CheckoutFields(_options, request);
        fields.Add(new("signature", PayfastSignature.ForCheckout(fields, _options.Passphrase)));
        var form = fields.Where(f => f.Value.Length > 0).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        return Task.FromResult(CheckoutStart.Go(new CheckoutRedirect(new Uri($"https://{_options.Host}/eng/process"), form)));
    }

    public async Task<GatewayNotification> ReadNotificationAsync(InboundNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var f = notification.Form;
        if (!f.TryGetValue("m_payment_id", out var reference) || !f.TryGetValue("pf_payment_id", out var payfastId)
            || !f.TryGetValue("payment_status", out var status) || !f.TryGetValue("signature", out var signature))
        {
            return GatewayNotification.Rejected("Not a Payfast ITN: m_payment_id, pf_payment_id, payment_status or signature is missing.");
        }

        if (!string.Equals(f.GetValueOrDefault("merchant_id"), _options.MerchantId, StringComparison.Ordinal))
        {
            return GatewayNotification.Rejected("The ITN is for another Payfast merchant.");
        }

        var parameters = PayfastSignature.NotificationParameters(notification.Body);
        if (!string.Equals(PayfastSignature.ForNotification(parameters, _options.Passphrase), signature, StringComparison.OrdinalIgnoreCase))
        {
            return GatewayNotification.Rejected("The ITN signature doesn't match (check the passphrase).");
        }

        if (_options.VerifySourceAddress && !await IsPayfastAddressAsync(notification.RemoteIp, cancellationToken))
        {
            return GatewayNotification.Rejected($"The ITN came from {notification.RemoteIp ?? "an unknown address"}, which isn't Payfast's.");
        }

        if (!await ConfirmedByPayfastAsync(parameters, cancellationToken))
        {
            return GatewayNotification.Rejected("Payfast didn't confirm the ITN as VALID.");
        }

        var kind = status switch
        {
            "COMPLETE" => GatewayNotificationKind.Paid,
            "CANCELLED" => GatewayNotificationKind.Cancelled,
            "FAILED" => GatewayNotificationKind.Failed,
            _ => GatewayNotificationKind.Other,
        };
        decimal? amount = decimal.TryParse(f.GetValueOrDefault("amount_gross"), NumberStyles.Number, CultureInfo.InvariantCulture, out var gross) ? gross : null;
        var fee = f.GetValueOrDefault("amount_fee");
        return new GatewayNotification(
            true,
            payfastId + ":" + status,
            reference,
            payfastId,
            kind,
            amount,
            kind == GatewayNotificationKind.Failed ? "Payfast reported the payment as failed." : $"Payfast {status}{(string.IsNullOrEmpty(fee) ? string.Empty : ", fee " + fee)}",
            f.GetValueOrDefault("token") is { Length: > 0 } token ? token : null);
    }

    /// <summary>
    /// Cancels a Payfast subscription through Payfast's API: <c>PUT /subscriptions/{token}/cancel</c>, signed with the
    /// merchant ID, version and timestamp headers (<c>?testing=true</c> against the sandbox).
    /// </summary>
    public decimal MinimumCharge => MinimumAmount;

    public decimal? MaximumCharge => _options.MaxAmount;

    public decimal? MaximumRecurringCharge => _options.MaxRecurringAmount;

    public bool SupportsRecurringUpdate => true;

    public Task<GatewayResult> CancelRecurringAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return CallSubscriptionApiAsync(HttpMethod.Put, token, "cancel", new Dictionary<string, long>(StringComparer.Ordinal), cancellationToken);
    }

    /// <summary>
    /// Payfast's subscription update: the amount (in cents) and frequency charged from the next run on; the run date
    /// stays as Payfast has it.
    /// </summary>
    public Task<GatewayResult> UpdateRecurringAsync(string token, decimal amount, Domain.Subscriptions.BillingCycle cycle, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var body = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["amount"] = (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero),
            ["frequency"] = cycle == Domain.Subscriptions.BillingCycle.Annual ? 6 : 3,
        };
        return CallSubscriptionApiAsync(HttpMethod.Patch, token, "update", body, cancellationToken);
    }

    /// <summary>One call to Payfast's subscriptions API, signed over the headers and body values.</summary>
    private async Task<GatewayResult> CallSubscriptionApiAsync(HttpMethod method, string token, string action, Dictionary<string, long> body, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant-id"] = _options.MerchantId,
            ["version"] = "v1",
            ["timestamp"] = time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        };
        using var request = new HttpRequestMessage(method, new Uri($"https://api.payfast.co.za/subscriptions/{Uri.EscapeDataString(token)}/{action}{(_options.Sandbox ? "?testing=true" : string.Empty)}"));
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body.Count > 0)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        var signed = new Dictionary<string, string>(headers, StringComparer.Ordinal);
        foreach (var value in body)
        {
            signed[value.Key] = value.Value.ToString(CultureInfo.InvariantCulture);
        }

        request.Headers.TryAddWithoutValidation("signature", PayfastSignature.ForApi(signed, _options.Passphrase));
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var answer = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode && answer.Contains("\"success\"", StringComparison.OrdinalIgnoreCase))
            {
                return GatewayResult.Ok();
            }

            LogApiRefused(logger, action, (int)response.StatusCode, answer.Length > 300 ? answer[..300] : answer);
            return GatewayResult.Failed($"Payfast answered {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogApiUnreachable(logger, action, ex);
            return GatewayResult.Failed("Payfast couldn't be reached.");
        }
    }

    /// <summary>Payfast's page where the payer changes the card a subscription charges, then comes back.</summary>
    public Uri? UpdateCardUrl(string token, Uri returnUrl)
    {
        ArgumentNullException.ThrowIfNull(returnUrl);
        return new Uri($"https://{_options.Host}/eng/recurring/update/{Uri.EscapeDataString(token)}?return={PayfastSignature.Encode(returnUrl.AbsoluteUri)}");
    }

    /// <summary>The checkout fields, in the order Payfast's documentation lists them (the signature depends on it).</summary>
    internal static List<KeyValuePair<string, string>> CheckoutFields(PayfastOptions options, CheckoutRequest request) =>
    [
        new("merchant_id", options.MerchantId),
        new("merchant_key", options.MerchantKey),
        new("return_url", request.ReturnUrl.AbsoluteUri),
        new("cancel_url", request.CancelUrl.AbsoluteUri),
        new("notify_url", request.NotifyUrl.AbsoluteUri),
        new("email_address", Limit(request.CustomerEmail, 100)),
        new("m_payment_id", request.Reference),
        new("amount", request.Amount.ToString("0.00", CultureInfo.InvariantCulture)),
        new("item_name", Limit(request.Description, 100)),
        new("payment_method", options.PaymentMethod?.Trim() ?? string.Empty),

        // Subscriptions (recurring billing): charged every month or year from today, until cancelled.
        new("subscription_type", request.RenewEvery is null ? string.Empty : "1"),
        new("recurring_amount", request.RenewEvery is null ? string.Empty : request.Amount.ToString("0.00", CultureInfo.InvariantCulture)),
        new("frequency", request.RenewEvery switch { null => string.Empty, Domain.Subscriptions.BillingCycle.Annual => "6", _ => "3" }),
        new("cycles", request.RenewEvery is null ? string.Empty : "0"),
    ];

    private async Task<bool> IsPayfastAddressAsync(string? remoteIp, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(remoteIp, out var address))
        {
            return false;
        }

        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (_options.AllowedSourceAddresses.Any(allowed => Matches(allowed, address)))
        {
            return true;
        }

        foreach (var host in NotificationHosts)
        {
            try
            {
                if ((await Dns.GetHostAddressesAsync(host, cancellationToken)).Any(a => a.Equals(address)))
                {
                    return true;
                }
            }
            catch (SocketException ex)
            {
                LogLookupFailed(logger, host, ex);
            }
        }

        return false;
    }

    private async Task<bool> ConfirmedByPayfastAsync(string parameters, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(parameters, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await httpClients.CreateClient(HttpClientName).PostAsync(new Uri($"https://{_options.Host}/eng/query/validate"), content, cancellationToken);
            var answer = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            return response.IsSuccessStatusCode && string.Equals(answer, "VALID", StringComparison.Ordinal);
        }
        catch (HttpRequestException ex)
        {
            // Unconfirmed: answered 400, so Payfast sends it again and it is checked again.
            LogConfirmationFailed(logger, ex);
            return false;
        }
    }

    private static bool Matches(string allowed, IPAddress address)
    {
        if (IPNetwork.TryParse(allowed, out var network))
        {
            return network.Contains(address);
        }

        return IPAddress.TryParse(allowed, out var single) && single.Equals(address);
    }

    private static string Limit(string? value, int max) => value is null ? string.Empty : value.Length > max ? value[..max] : value;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't look up {Host} to check where a Payfast ITN came from")]
    private static partial void LogLookupFailed(ILogger logger, string host, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't reach Payfast to confirm an ITN; it will be retried")]
    private static partial void LogConfirmationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payfast refused the subscription {Action}: {Status} {Body}")]
    private static partial void LogApiRefused(ILogger logger, string action, int status, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't reach Payfast for the subscription {Action}")]
    private static partial void LogApiUnreachable(ILogger logger, string action, Exception exception);
}

/// <summary>Payfast's site answers. Degraded, not unhealthy: only online payments wait for it.</summary>
internal sealed class PayfastHealthCheck(IHttpClientFactory httpClients, IOptions<PayfastOptions> options) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var host = options.Value.Host;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, new Uri($"https://{host}/"));
            using var response = await httpClients.CreateClient(PayfastGateway.HttpClientName).SendAsync(request, cancellationToken);
            return (int)response.StatusCode < 500
                ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy($"{host} is reachable.")
                : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded($"{host} answered {(int)response.StatusCode}; online payments may fail.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded($"{host} is unreachable; online payments can't start.", exception);
        }
    }
}
