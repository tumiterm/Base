using System.Net;
using System.Text;
using System.Text.Json;
using Base.Application.Payments;
using Base.Infrastructure.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Base.IntegrationTests.Payments;

/// <summary>
/// The Yoco adapter. The expected signature was computed by an independent Python implementation of the Standard
/// Webhooks scheme (HMAC-SHA256 over <c>id.timestamp.body</c> with the base64-decoded secret) for these exact values.
/// </summary>
public sealed class YocoTests
{
    private const string WebhookId = "evt_2Kp9SqFz";
    private const string Timestamp = "1768464000";
    private const string ExpectedV1 = "SV4rfYdB/s8FYYlYCzTfgGdA/tEnZ4x+bx3dnR6NETo=";

    private const string Body =
        """{"id":"evt_2Kp9SqFz","type":"payment.succeeded","createdDate":"2026-01-15T08:00:00.000Z","payload":{"id":"p_8Wx","type":"payment","amount":49900,"currency":"ZAR","status":"succeeded","mode":"test","metadata":{"checkoutId":"ch_QmZ3","reference":"P7K2M9QX4T3B"}}}""";

    private static readonly string Secret = "whsec_" + Convert.ToBase64String("base-tests-yoco-webhook-secret!!"u8.ToArray());

    private static readonly CheckoutRequest Checkout = new(
        "P7K2M9QX4T3B",
        499m,
        "ZAR",
        "Starter, 1 month (Acme)",
        "owner@acme.test",
        "Acme",
        new Uri("https://acme.example.com/subscription/payments/P7K2M9QX4T3B"),
        new Uri("https://acme.example.com/subscription/payments/P7K2M9QX4T3B?cancelled=true"),
        new Uri("https://base.example.com/payments/notify/yoco"));

    [Fact]
    public void Signatures_follow_standard_webhooks()
    {
        YocoSignature.Sign(Secret, WebhookId, Timestamp, Body).ShouldBe(ExpectedV1);
        YocoSignature.Verify(Secret, WebhookId, Timestamp, Body, "v1,wrong v1," + ExpectedV1).ShouldBeTrue("any listed signature may match while a secret rotates");
        YocoSignature.Verify(Secret, WebhookId, Timestamp, Body + " ", "v1," + ExpectedV1).ShouldBeFalse();
        YocoSignature.Verify(Secret, WebhookId, Timestamp, Body, "v2," + ExpectedV1).ShouldBeFalse();
        YocoSignature.Verify("not base64!", WebhookId, Timestamp, Body, "v1," + ExpectedV1).ShouldBeFalse();
    }

    [Fact]
    public async Task A_checkout_is_created_with_the_secret_key_in_cents_and_sends_the_person_to_yocos_page()
    {
        var clients = new Capture(HttpStatusCode.OK, """{"id":"ch_QmZ3","redirectUrl":"https://c.yoco.com/checkout/ch_QmZ3","status":"created"}""");
        var start = await Gateway(clients).StartCheckoutAsync(Checkout, CancellationToken.None);

        start.ShouldSatisfyAllConditions(
            s => s.Redirect!.Url.AbsoluteUri.ShouldBe("https://c.yoco.com/checkout/ch_QmZ3"),
            s => s.Redirect!.IsFormPost.ShouldBeFalse(),
            s => s.GatewayReference.ShouldBe("ch_QmZ3"));
        var sent = clients.Last!;
        sent.RequestUri!.AbsoluteUri.ShouldBe("https://payments.yoco.com/api/checkouts");
        sent.Headers.Authorization!.ToString().ShouldBe("Bearer sk_test_base_tests");
        sent.Headers.GetValues("Idempotency-Key").ShouldBe(["P7K2M9QX4T3B"]);
        using var json = JsonDocument.Parse(clients.LastBody!);
        json.RootElement.GetProperty("amount").GetInt64().ShouldBe(49900);
        json.RootElement.GetProperty("currency").GetString().ShouldBe("ZAR");
        json.RootElement.GetProperty("successUrl").GetString().ShouldBe(Checkout.ReturnUrl.AbsoluteUri);
        json.RootElement.GetProperty("cancelUrl").GetString().ShouldBe(Checkout.CancelUrl.AbsoluteUri);
        json.RootElement.GetProperty("metadata").GetProperty("reference").GetString().ShouldBe("P7K2M9QX4T3B");

        var refused = await Gateway(new Capture(HttpStatusCode.Unauthorized, """{"errorMessage":"bad key"}""")).StartCheckoutAsync(Checkout, CancellationToken.None);
        refused.Error.ShouldBe("Yoco couldn't start the payment (401).");
        (await Gateway().StartCheckoutAsync(Checkout with { Amount = 1.99m }, CancellationToken.None)).Error.ShouldBe("Yoco can't take payments under R2.00.");
    }

    [Fact]
    public async Task A_signed_recent_webhook_is_read_and_anything_else_is_rejected()
    {
        var read = await Gateway().ReadNotificationAsync(Webhook(Body, ExpectedV1), CancellationToken.None);
        read.ShouldSatisfyAllConditions(
            r => r.Verified.ShouldBeTrue(r.Detail),
            r => r.Kind.ShouldBe(GatewayNotificationKind.Paid),
            r => r.EventId.ShouldBe(WebhookId),
            r => r.Reference.ShouldBe("P7K2M9QX4T3B"),
            r => r.CheckoutId.ShouldBe("ch_QmZ3"),
            r => r.Amount.ShouldBe(499.00m));

        (await Gateway().ReadNotificationAsync(Webhook(Body.Replace("49900", "100", StringComparison.Ordinal), ExpectedV1), CancellationToken.None))
            .Detail.ShouldBe("The webhook signature doesn't match (check the webhook secret).");
        (await Gateway(now: DateTimeOffset.FromUnixTimeSeconds(1768464000).AddMinutes(10)).ReadNotificationAsync(Webhook(Body, ExpectedV1), CancellationToken.None))
            .Detail!.ShouldContain("too far from now");
        (await Gateway().ReadNotificationAsync(new InboundNotification(Body, new Dictionary<string, string>(), new Dictionary<string, string>(), null), CancellationToken.None))
            .Verified.ShouldBeFalse();

        var other = """{"id":"evt_x","type":"refund.succeeded","payload":{"id":"r_1"}}""";
        var signed = YocoSignature.Sign(Secret, WebhookId, Timestamp, other);
        (await Gateway().ReadNotificationAsync(Webhook(other, signed), CancellationToken.None)).Kind.ShouldBe(GatewayNotificationKind.Other);
    }

    [Fact]
    public async Task Without_a_webhook_yoco_is_asked_how_the_checkout_went()
    {
        var clients = new Capture(HttpStatusCode.OK, """{"id":"ch_QmZ3","status":"completed","amount":49900,"currency":"ZAR","paymentId":"p_8Wx","metadata":{"reference":"P7K2M9QX4T3B"}}""");
        var read = await Gateway(clients).CheckAsync("P7K2M9QX4T3B", "ch_QmZ3", CancellationToken.None);
        read.ShouldSatisfyAllConditions(
            r => r.Verified.ShouldBeTrue(),
            r => r.Kind.ShouldBe(GatewayNotificationKind.Paid),
            r => r.Reference.ShouldBe("P7K2M9QX4T3B"),
            r => r.Amount.ShouldBe(499m),
            r => r.EventId.ShouldBe("checkout:ch_QmZ3:completed"));
        clients.Last!.Method.ShouldBe(HttpMethod.Get);
        clients.Last.RequestUri!.AbsoluteUri.ShouldBe("https://payments.yoco.com/api/checkouts/ch_QmZ3");
        clients.Last.Headers.Authorization!.ToString().ShouldBe("Bearer sk_test_base_tests");

        (await Gateway(new Capture(HttpStatusCode.OK, """{"id":"ch_QmZ3","status":"started"}""")).CheckAsync("P7K2M9QX4T3B", "ch_QmZ3", CancellationToken.None))
            .Kind.ShouldBe(GatewayNotificationKind.Other, "still waiting");
        (await Gateway(new Capture(HttpStatusCode.NotFound, "{}")).CheckAsync("P7K2M9QX4T3B", "ch_QmZ3", CancellationToken.None)).Verified.ShouldBeFalse();
        (await Gateway().CheckAsync("P7K2M9QX4T3B", null, CancellationToken.None)).Verified.ShouldBeFalse();
    }

    private static InboundNotification Webhook(string body, string signature) =>
        new(
            body,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["webhook-id"] = WebhookId,
                ["webhook-timestamp"] = Timestamp,
                ["webhook-signature"] = "v1," + signature,
            },
            "203.0.113.9");

    private static YocoGateway Gateway(Capture? clients = null, DateTimeOffset? now = null) =>
        new(
            Options.Create(new YocoOptions { Enabled = true, SecretKey = "sk_test_base_tests", WebhookSecret = Secret }),
            clients ?? new Capture(HttpStatusCode.OK, "{}"),
            new FakeTimeProvider(now ?? DateTimeOffset.FromUnixTimeSeconds(1768464000).AddSeconds(30)),
            NullLogger<YocoGateway>.Instance);

    /// <summary>Stands in for Yoco's API, answering every request the same and remembering the last one.</summary>
    private sealed class Capture(HttpStatusCode status, string answer) : IHttpClientFactory
    {
        public HttpRequestMessage? Last { get; private set; }

        public string? LastBody { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(Capture owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Last = request;
                owner.LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(owner._status) { Content = new StringContent(owner._answer, Encoding.UTF8, "application/json") };
            }
        }

        private readonly HttpStatusCode _status = status;
        private readonly string _answer = answer;
    }
}
