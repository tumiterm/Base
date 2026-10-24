using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Base.Application.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Payments;

public sealed class YocoOptions
{
    public const string SectionName = "Payments:Gateways:Yoco";

    public bool Enabled { get; set; }

    /// <summary>From the Yoco App (Checkout API → How to connect): <c>sk_test_...</c> for testing, <c>sk_live_...</c> live.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// The <c>whsec_...</c> secret Yoco returns when the webhook is registered (see docs/payments.md). Required outside
    /// Development; without it, payments are confirmed only by asking Yoco when the person comes back.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>What the chooser shows; empty shows <see cref="DefaultMethods"/>.</summary>
    public IList<string> Methods { get; set; } = [];

    public static IReadOnlyList<string> DefaultMethods { get; } = ["Card"];

    public string ApiBaseUrl { get; set; } = "https://payments.yoco.com/api/";

    public bool IsTest => SecretKey.StartsWith("sk_test_", StringComparison.Ordinal);
}

/// <summary>
/// Yoco's Checkout API: the server creates a checkout and sends the person to Yoco's page; Yoco reports the outcome
/// with a webhook signed the Standard Webhooks way (<c>webhook-id</c>, <c>webhook-timestamp</c>, <c>webhook-signature</c>,
/// HMAC-SHA256). Once-off payments only.
/// </summary>
internal sealed partial class YocoGateway(IOptions<YocoOptions> options, IHttpClientFactory httpClients, TimeProvider time, ILogger<YocoGateway> logger) : IPaymentGateway
{
    public const string GatewayCode = "Yoco";
    public const string HttpClientName = "yoco";

    /// <summary>Yoco doesn't accept payments under R2.00.</summary>
    public const decimal MinimumAmount = 2m;

    /// <summary>How old a webhook may be, against replays.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly YocoOptions _options = options.Value;

    public string Code => GatewayCode;

    public string Name => _options.IsTest ? "Yoco (test)" : "Yoco";

    public IReadOnlyList<string> Methods => _options.Methods.Count > 0 ? [.. _options.Methods] : YocoOptions.DefaultMethods;

    public IReadOnlyList<string> Currencies { get; } = ["ZAR"];

    public decimal MinimumCharge => MinimumAmount;

    public async Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount < MinimumAmount)
        {
            return CheckoutStart.Failed("Yoco can't take payments under R2.00.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.ApiBaseUrl), "checkouts"))
        {
            // Serialized up front so the request has a Content-Length rather than a chunked body.
            Content = new StringContent(JsonSerializer.Serialize(
                new
                {
                    amount = (long)decimal.Round(request.Amount * 100m, 0, MidpointRounding.AwayFromZero),
                    currency = request.Currency,
                    successUrl = request.ReturnUrl.AbsoluteUri,
                    cancelUrl = request.CancelUrl.AbsoluteUri,
                    failureUrl = request.ReturnUrl.AbsoluteUri,
                    metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["reference"] = request.Reference, ["description"] = request.Description },
                },
                Json), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", request.Reference);

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogCheckoutRefused(logger, (int)response.StatusCode, Trim(body));
            return CheckoutStart.Failed($"Yoco couldn't start the payment ({(int)response.StatusCode}).");
        }

        using var created = JsonDocument.Parse(body);
        var root = created.RootElement;
        return root.TryGetProperty("redirectUrl", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var redirect)
            && root.TryGetProperty("id", out var id)
            ? CheckoutStart.Go(new CheckoutRedirect(redirect), id.GetString())
            : CheckoutStart.Failed("Yoco's answer had no checkout page to go to.");
    }

    public bool SupportsStatusCheck => true;

    /// <summary>
    /// Asks Yoco for the checkout (<c>GET checkouts/{id}</c> with the secret key): <c>completed</c> means paid. This is
    /// how payments are confirmed where Yoco's webhook can't reach the app, such as a developer's machine.
    /// </summary>
    public async Task<GatewayNotification> CheckAsync(string reference, string? gatewayReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gatewayReference))
        {
            return GatewayNotification.Rejected("No Yoco checkout to ask about.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_options.ApiBaseUrl), "checkouts/" + Uri.EscapeDataString(gatewayReference)));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogCheckRefused(logger, (int)response.StatusCode, Trim(body));
            return GatewayNotification.Rejected($"Yoco didn't answer about the checkout ({(int)response.StatusCode}).");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var status = Text(root, "status");
            var kind = status switch
            {
                "completed" => GatewayNotificationKind.Paid,
                "failed" => GatewayNotificationKind.Failed,
                _ => GatewayNotificationKind.Other,
            };
            var metadata = root.TryGetProperty("metadata", out var m) ? m : default;
            decimal? amount = root.TryGetProperty("amount", out var cents) && cents.TryGetInt64(out var c) ? c / 100m : null;
            return new GatewayNotification(
                true,
                $"checkout:{gatewayReference}:{status}",
                Text(metadata, "reference") ?? reference,
                Text(root, "paymentId") ?? gatewayReference,
                kind,
                amount,
                kind == GatewayNotificationKind.Failed ? "Yoco reported the payment as failed." : $"Yoco checkout {status}",
                CheckoutId: gatewayReference);
        }
        catch (JsonException)
        {
            return GatewayNotification.Rejected("Yoco's answer isn't JSON.");
        }
    }

    public Task<GatewayNotification> ReadNotificationAsync(InboundNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var headers = notification.Headers;
        if (!headers.TryGetValue("webhook-id", out var id) || !headers.TryGetValue("webhook-timestamp", out var stamp) || !headers.TryGetValue("webhook-signature", out var signatures))
        {
            return Task.FromResult(GatewayNotification.Rejected("Not a Yoco webhook: webhook-id, webhook-timestamp or webhook-signature is missing."));
        }

        if (!long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || (time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return Task.FromResult(GatewayNotification.Rejected("The webhook's timestamp is too far from now (a replay, or a clock problem)."));
        }

        if (!YocoSignature.Verify(_options.WebhookSecret, id, stamp, notification.Body, signatures))
        {
            return Task.FromResult(GatewayNotification.Rejected("The webhook signature doesn't match (check the webhook secret)."));
        }

        try
        {
            using var document = JsonDocument.Parse(notification.Body);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var payload = root.TryGetProperty("payload", out var p) ? p : default;
            var metadata = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("metadata", out var m) ? m : default;
            var kind = type switch
            {
                "payment.succeeded" => GatewayNotificationKind.Paid,
                "payment.failed" => GatewayNotificationKind.Failed,
                _ => GatewayNotificationKind.Other,
            };
            decimal? amount = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("amount", out var cents) && cents.TryGetInt64(out var c) ? c / 100m : null;
            var checkoutId = Text(metadata, "checkoutId");
            return Task.FromResult(new GatewayNotification(
                true,
                id,
                Text(metadata, "reference"),
                checkoutId,
                kind,
                amount,
                kind == GatewayNotificationKind.Failed ? "Yoco reported the payment as failed." : $"Yoco {type}",
                CheckoutId: checkoutId));
        }
        catch (JsonException)
        {
            return Task.FromResult(GatewayNotification.Rejected("The webhook body isn't JSON."));
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Trim(string body) => body.Length > 300 ? body[..300] : body;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Yoco wouldn't say how a checkout went: {Status} {Body}")]
    private static partial void LogCheckRefused(ILogger logger, int status, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Yoco refused to create a checkout: {Status} {Body}")]
    private static partial void LogCheckoutRefused(ILogger logger, int status, string body);
}

/// <summary>
/// Standard Webhooks signatures, as Yoco uses them: HMAC-SHA256 of <c>{id}.{timestamp}.{body}</c> with the secret's
/// bytes (base64 after <c>whsec_</c>), base64-encoded, sent as <c>v1,{signature}</c> (several may be listed, space
/// separated, while a secret is rotated).
/// </summary>
public static class YocoSignature
{
    public static string Sign(string secret, string id, string timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret);
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
    }

    public static bool Verify(string secret, string id, string timestamp, string body, string signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Encoding.ASCII.GetBytes(Sign(secret, id, timestamp, body));
        }
        catch (FormatException)
        {
            return false;
        }

        return signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split(',', 2))
            .Any(s => s.Length == 2 && string.Equals(s[0], "v1", StringComparison.Ordinal)
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s[1]), expected));
    }
}

/// <summary>Yoco's API answers. Degraded, not unhealthy: only online payments wait for it.</summary>
internal sealed class YocoHealthCheck(IHttpClientFactory httpClients, IOptions<YocoOptions> options) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var api = new Uri(options.Value.ApiBaseUrl);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, api);
            using var response = await httpClients.CreateClient(YocoGateway.HttpClientName).SendAsync(request, cancellationToken);
            return (int)response.StatusCode < 500
                ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy($"{api.Host} is reachable.")
                : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded($"{api.Host} answered {(int)response.StatusCode}; online payments may fail.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded($"{api.Host} is unreachable; online payments can't start.", exception);
        }
    }
}
