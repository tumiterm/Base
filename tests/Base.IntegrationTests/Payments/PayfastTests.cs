using System.Net;
using Base.Application.Payments;
using Base.Infrastructure.Payments;
using Base.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.Payments;

/// <summary>
/// The Payfast adapter against Payfast's own rules. The expected signatures were produced by the PHP samples in
/// Payfast's documentation (generateSignature, and the ITN parameter string with pfValidSignature) run on PHP 8.3 with
/// these exact values, so a difference in encoding or field order shows up here.
/// </summary>
public sealed class PayfastTests
{
    private const string Passphrase = "jt7NOE43FZPn";
    private const string ItemName = "Pro, 1 year (Acme & Sons ~ Café*)";

    private const string ItnBody =
        "m_payment_id=P7K2M9QX4T3B&pf_payment_id=1089250&payment_status=COMPLETE&item_name=Pro%2C+1+year+%28Acme+%26+Sons+%7E+Caf%C3%A9%2A%29" +
        "&item_description=&amount_gross=3000.00&amount_fee=-69.00&amount_net=2931.00&custom_str1=&name_first=&email_address=owner%2Bbilling%40acme.test" +
        "&merchant_id=10000100&signature=f44ee63ed097b7c35218f577712492cb";

    private static readonly CheckoutRequest Checkout = new(
        "P7K2M9QX4T3B",
        3000m,
        "ZAR",
        ItemName,
        "owner+billing@acme.test",
        "Acme",
        new Uri("https://acme.example.com/subscription/payments/P7K2M9QX4T3B"),
        new Uri("https://acme.example.com/subscription/payments/P7K2M9QX4T3B?cancelled=true"),
        new Uri("https://base.example.com/payments/notify/payfast"));

    [Theory]
    [InlineData("abc-XYZ_0.9", "abc-XYZ_0.9")]
    [InlineData("a b", "a+b")]
    [InlineData("https://x.co/a?b=c&d", "https%3A%2F%2Fx.co%2Fa%3Fb%3Dc%26d")]
    [InlineData("~!*()'", "%7E%21%2A%28%29%27")]
    [InlineData("Café", "Caf%C3%A9")]
    public void Values_are_encoded_like_php_urlencode(string value, string expected) =>
        PayfastSignature.Encode(value).ShouldBe(expected);

    [Fact]
    public void The_checkout_signature_matches_payfasts_php_sample_with_and_without_a_passphrase()
    {
        var fields = PayfastGateway.CheckoutFields(Options().Value, Checkout);
        PayfastSignature.ForCheckout(fields, Passphrase).ShouldBe("114160cdc8e816f891c308875a1fb514");
        PayfastSignature.ForCheckout(fields, null).ShouldBe("f584322f605ac08fffcc46d811b3e0e5");
    }

    [Fact]
    public async Task The_checkout_is_a_signed_form_posted_to_payfast_without_blank_fields()
    {
        var gateway = Gateway(Options(sandbox: true), "VALID");
        var start = await gateway.StartCheckoutAsync(Checkout, CancellationToken.None);

        var redirect = start.Redirect.ShouldNotBeNull();
        redirect.Url.AbsoluteUri.ShouldBe("https://sandbox.payfast.co.za/eng/process");
        redirect.FormFields!["signature"].ShouldBe("114160cdc8e816f891c308875a1fb514");
        redirect.FormFields["amount"].ShouldBe("3000.00");
        redirect.FormFields.ShouldNotContainKey("payment_method");

        (await gateway.StartCheckoutAsync(Checkout with { Amount = 4.99m }, CancellationToken.None)).Error.ShouldBe("Payfast can't take payments under R5.00.");
    }

    [Fact]
    public async Task A_genuine_itn_is_read_after_payfast_confirms_it()
    {
        var gateway = Gateway(Options(), "VALID");
        var read = await gateway.ReadNotificationAsync(Itn(ItnBody), CancellationToken.None);

        read.ShouldSatisfyAllConditions(
            r => r.Verified.ShouldBeTrue(r.Detail),
            r => r.Kind.ShouldBe(GatewayNotificationKind.Paid),
            r => r.Reference.ShouldBe("P7K2M9QX4T3B"),
            r => r.GatewayReference.ShouldBe("1089250"),
            r => r.EventId.ShouldBe("1089250:COMPLETE"),
            r => r.Amount.ShouldBe(3000.00m));
        PayfastSignature.NotificationParameters(ItnBody).ShouldEndWith("&merchant_id=10000100", Case.Sensitive, "the signature and anything after it are left out");
    }

    [Fact]
    public async Task An_itn_that_was_altered_is_unconfirmed_or_from_elsewhere_is_rejected()
    {
        var tampered = ItnBody.Replace("amount_gross=3000.00", "amount_gross=1.00", StringComparison.Ordinal);
        (await Gateway(Options(), "VALID").ReadNotificationAsync(Itn(tampered), CancellationToken.None)).Detail.ShouldBe("The ITN signature doesn't match (check the passphrase).");
        (await Gateway(Options(passphrase: "wrong"), "VALID").ReadNotificationAsync(Itn(ItnBody), CancellationToken.None)).Verified.ShouldBeFalse();
        (await Gateway(Options(), "INVALID").ReadNotificationAsync(Itn(ItnBody), CancellationToken.None)).Detail.ShouldBe("Payfast didn't confirm the ITN as VALID.");
        (await Gateway(Options(merchantId: "10000999"), "VALID").ReadNotificationAsync(Itn(ItnBody), CancellationToken.None)).Detail.ShouldBe("The ITN is for another Payfast merchant.");
        (await Gateway(Options(), "VALID").ReadNotificationAsync(Itn("hello=world"), CancellationToken.None)).Verified.ShouldBeFalse();

        var checkedSource = Options(verifySource: true, allowed: ["203.0.113.0/24"]);
        (await Gateway(checkedSource, "VALID").ReadNotificationAsync(Itn(ItnBody, "203.0.113.9"), CancellationToken.None)).Verified.ShouldBeTrue("a listed range is accepted");
        (await Gateway(checkedSource, "VALID").ReadNotificationAsync(Itn(ItnBody, "::ffff:203.0.113.9"), CancellationToken.None)).Verified.ShouldBeTrue("IPv4 mapped into IPv6 too");
        (await Gateway(checkedSource, "VALID").ReadNotificationAsync(Itn(ItnBody, "198.51.100.1"), CancellationToken.None)).Detail!.ShouldContain("isn't Payfast's");
    }

    [Fact]
    public async Task Automatic_renewal_adds_payfasts_subscription_fields_signed_like_its_php_sample_and_needs_the_passphrase()
    {
        var renewing = Checkout with { RenewEvery = Base.Domain.Subscriptions.BillingCycle.Annual };
        var fields = PayfastGateway.CheckoutFields(Options().Value, renewing);
        PayfastSignature.ForCheckout(fields, Passphrase).ShouldBe("fadc331897181919bfb121855e65cafd");

        var form = (await Gateway(Options(), "VALID").StartCheckoutAsync(renewing, CancellationToken.None)).Redirect!.FormFields!;
        form["subscription_type"].ShouldBe("1");
        form["frequency"].ShouldBe("6", "annual");
        form["cycles"].ShouldBe("0", "until cancelled");
        form["recurring_amount"].ShouldBe("3000.00");
        (await Gateway(Options(passphrase: string.Empty), "VALID").StartCheckoutAsync(renewing, CancellationToken.None)).Error!.ShouldContain("passphrase");

        PayfastGateway.CheckoutFields(Options().Value, Checkout with { RenewEvery = Base.Domain.Subscriptions.BillingCycle.Monthly })
            .Single(f => f.Key == "frequency").Value.ShouldBe("3", "monthly");
    }

    [Fact]
    public async Task A_renewal_itn_carries_its_token_and_cancelling_calls_payfasts_api_signed_alphabetically()
    {
        var withToken = ItnBody.Replace("&signature=f44ee63ed097b7c35218f577712492cb", string.Empty, StringComparison.Ordinal) + "&token=dc0521d3-55fe-269b-fa00-b647310d760f";
        var signed = withToken + "&signature=" + PayfastSignature.ForNotification(PayfastSignature.NotificationParameters(withToken), Passphrase);
        (await Gateway(Options(), "VALID").ReadNotificationAsync(Itn(signed), CancellationToken.None)).RecurringToken.ShouldBe("dc0521d3-55fe-269b-fa00-b647310d760f");

        PayfastSignature.ForApi(new Dictionary<string, string>(StringComparer.Ordinal) { ["version"] = "v1", ["merchant-id"] = "10000100", ["timestamp"] = "2026-01-15T08:00:00+00:00" }, Passphrase)
            .ShouldBe("88979cb6250ce58b1c5d932585033d2d", "PHP: ksort + http_build_query with the passphrase");

        var clients = new AnsweringClients("""{"code":200,"status":"success","data":{"response":true}}""");
        (await Gateway(Options(sandbox: true), "", clients).CancelRecurringAsync("dc0521d3-55fe-269b-fa00-b647310d760f", CancellationToken.None)).Succeeded.ShouldBeTrue();
        var sent = clients.Last!;
        sent.Method.ShouldBe(HttpMethod.Put);
        sent.RequestUri!.AbsoluteUri.ShouldBe("https://api.payfast.co.za/subscriptions/dc0521d3-55fe-269b-fa00-b647310d760f/cancel?testing=true");
        sent.Headers.GetValues("merchant-id").ShouldBe(["10000100"]);
        sent.Headers.GetValues("timestamp").ShouldBe(["2026-01-15T08:00:00+00:00"]);
        sent.Headers.GetValues("signature").ShouldBe(["88979cb6250ce58b1c5d932585033d2d"]);

        var refused = new AnsweringClients("""{"code":404,"status":"failed"}""", HttpStatusCode.NotFound);
        (await Gateway(Options(), "", refused).CancelRecurringAsync("nope", CancellationToken.None)).Error.ShouldBe("Payfast answered 404.");

        Gateway(Options(sandbox: true), "VALID").UpdateCardUrl("dc0521d3-55fe-269b-fa00-b647310d760f", new Uri("https://acme.example.com/subscription"))!.AbsoluteUri
            .ShouldBe("https://sandbox.payfast.co.za/eng/recurring/update/dc0521d3-55fe-269b-fa00-b647310d760f?return=https%3A%2F%2Facme.example.com%2Fsubscription");
    }

    private static InboundNotification Itn(string body, string? remoteIp = "203.0.113.9") =>
        new(
            body,
            body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : string.Empty, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            remoteIp);

    private static IOptions<PayfastOptions> Options(bool sandbox = false, string passphrase = Passphrase, string merchantId = "10000100", bool verifySource = false, IList<string>? allowed = null) =>
        Microsoft.Extensions.Options.Options.Create(new PayfastOptions
        {
            Enabled = true,
            MerchantId = merchantId,
            MerchantKey = "46f0cd694581a",
            Passphrase = passphrase,
            Sandbox = sandbox,
            VerifySourceAddress = verifySource,
            AllowedSourceAddresses = allowed ?? [],
        });

    private static PayfastGateway Gateway(IOptions<PayfastOptions> options, string answer, AnsweringClients? clients = null) =>
        new(options, clients ?? new AnsweringClients(answer), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero)), NullLogger<PayfastGateway>.Instance);

    /// <summary>Stands in for Payfast's servers, answering every request the same and remembering the last one.</summary>
    private sealed class AnsweringClients(string answer, HttpStatusCode status = HttpStatusCode.OK) : IHttpClientFactory
    {
        public HttpRequestMessage? Last { get; private set; }

        public HttpClient CreateClient(string name) => new(new Answer(this));

        private sealed class Answer(AnsweringClients owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Last = request;
                return Task.FromResult(new HttpResponseMessage(owner._status) { Content = new StringContent(owner._answer) });
            }
        }

        private readonly string _answer = answer;
        private readonly HttpStatusCode _status = status;
    }
}

/// <summary>Payfast is offered only once configured, and only then may the browser post to it.</summary>
public sealed class PayfastRegistrationTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Configuring_payfast_offers_it_for_rand_plans_and_lets_the_browser_post_to_its_sandbox()
    {
        await using var app = await TestApp.CreateAsync(sql.NewDatabase(), settings: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Payments:Gateways:Payfast:Enabled"] = "true",
            ["Payments:Gateways:Payfast:MerchantId"] = "10000100",
            ["Payments:Gateways:Payfast:MerchantKey"] = "46f0cd694581a",
            ["Payments:Gateways:Payfast:Sandbox"] = "true",
            ["Payments:Gateways:Payfast:Methods:0"] = "Card",
            ["Payments:Gateways:Payfast:Methods:1"] = "Apple Pay",
        });

        var gateways = await app.InScopeAsync(app.DefaultTenantId, sp => Task.FromResult(sp.GetRequiredService<IPaymentService>().Gateways("ZAR")));
        gateways.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            g => g.Code.ShouldBe("Payfast"),
            g => g.Name.ShouldBe("Payfast (sandbox)"),
            g => g.Methods.ShouldBe(["Card", "Apple Pay"]));
        app.Services.GetRequiredService<PaymentCheckoutOrigins>().Origins.ShouldBe(["https://sandbox.payfast.co.za"]);
        (await app.InScopeAsync(app.DefaultTenantId, sp => Task.FromResult(sp.GetRequiredService<IPaymentService>().Gateways("USD")))).ShouldBeEmpty("Payfast takes rand only");
    }
}
