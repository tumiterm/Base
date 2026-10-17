using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Base.Application.Notifications;
using Base.Domain.Notifications;
using Base.Infrastructure.Notifications;
using Base.Infrastructure.Notifications.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Base.IntegrationTests.Notifications;

/// <summary>
/// The HTTP request each provider sends and how it reads the answer, against a fake handler. These pin the documented
/// API shapes; they don't prove a live account works (do that once per project with real credentials).
/// </summary>
public sealed class ProviderRequestTests
{
    private static readonly Guid MessageId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly WhatsAppTemplate Template = new("general_reminder", "en", ["RecipientName", "Title", "Message"]);

    [Fact]
    public async Task SendGrid_posts_mail_send_with_bearer_auth()
    {
        using var http = new FakeHttp(HttpStatusCode.Accepted, "", ("X-Message-Id", "sg-123"));
        var provider = new SendGridEmailProvider(http, "sendgrid", new SendGridOptions { ApiKey = "SG.key", FromAddress = "noreply@acme.co", FromName = "Acme" });

        var result = await provider.SendAsync(Email(), CancellationToken.None);

        result.ShouldBe(ProviderResult.Sent("sg-123"));
        http.Request.RequestUri.ShouldBe(new Uri("https://api.sendgrid.com/v3/mail/send"));
        http.Request.Headers.Authorization!.ToString().ShouldBe("Bearer SG.key");
        var body = http.Json;
        body["personalizations"]![0]!["to"]![0]!["email"]!.GetValue<string>().ShouldBe("ann@example.com");
        body["from"]!["email"]!.GetValue<string>().ShouldBe("noreply@acme.co");
        body["subject"]!.GetValue<string>().ShouldBe("Hello");
        body["content"]!.AsArray().Select(c => c!["type"]!.GetValue<string>()).ShouldBe(["text/plain", "text/html"]);
    }

    [Fact]
    public async Task Twilio_sms_posts_a_form_with_basic_auth()
    {
        using var http = new FakeHttp(HttpStatusCode.Created, """{"sid":"SM123","status":"queued"}""");
        var provider = new TwilioSmsProvider(http, "twilio", new TwilioOptions { AccountSid = "AC1", AuthToken = "secret", From = "+14155550100" });

        var result = await provider.SendAsync(Sms(), CancellationToken.None);

        result.ShouldBe(ProviderResult.Sent("SM123"));
        http.Request.RequestUri.ShouldBe(new Uri("https://api.twilio.com/2010-04-01/Accounts/AC1/Messages.json"));
        http.Request.Headers.Authorization!.Scheme.ShouldBe("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(http.Request.Headers.Authorization.Parameter!)).ShouldBe("AC1:secret");
        http.Form.ShouldBe(new Dictionary<string, string>(StringComparer.Ordinal) { ["To"] = "+27821234567", ["Body"] = "Your code", ["From"] = "+14155550100" });
    }

    [Fact]
    public async Task Twilio_whatsapp_sends_the_approved_content_template()
    {
        using var http = new FakeHttp(HttpStatusCode.Created, """{"sid":"MM9"}""");
        var options = new TwilioOptions { AccountSid = "AC1", AuthToken = "t", MessagingServiceSid = "MG1" };
        options.ContentSids["general_reminder"] = "HX42";
        var provider = new TwilioWhatsAppProvider(http, "twilio-wa", options);

        (await provider.SendAsync(WhatsApp(), CancellationToken.None)).Succeeded.ShouldBeTrue();

        http.Form["To"].ShouldBe("whatsapp:+27821234567");
        http.Form["ContentSid"].ShouldBe("HX42");
        http.Form["MessagingServiceSid"].ShouldBe("MG1");
        JsonNode.Parse(http.Form["ContentVariables"])!["2"]!.GetValue<string>().ShouldBe("Pay rent");
    }

    [Fact]
    public async Task Twilio_whatsapp_without_a_content_sid_fails_permanently_without_calling_out()
    {
        using var http = new FakeHttp(HttpStatusCode.Created, "{}");
        var provider = new TwilioWhatsAppProvider(http, "twilio-wa", new TwilioOptions { AccountSid = "AC1", AuthToken = "t", From = "+1" });

        var result = await provider.SendAsync(WhatsApp(), CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.IsRetryable.ShouldBeFalse();
        result.Error!.ShouldContain("ContentSids");
        http.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Clickatell_posts_the_one_api_message_and_reads_acceptance()
    {
        using var http = new FakeHttp(HttpStatusCode.Accepted, """{"messages":[{"apiMessageId":"ck-1","accepted":true,"to":"27821234567"}]}""");
        var provider = new ClickatellSmsProvider(http, "clickatell", new ClickatellOptions { ApiKey = "ck-key" });

        (await provider.SendAsync(Sms(), CancellationToken.None)).ShouldBe(ProviderResult.Sent("ck-1"));
        http.Request.RequestUri.ShouldBe(new Uri("https://platform.clickatell.com/v1/message"));
        http.Request.Headers.Authorization!.ToString().ShouldBe("ck-key");
        var sms = http.Json["messages"]![0]!;
        sms["channel"]!.GetValue<string>().ShouldBe("sms");
        sms["to"]!.GetValue<string>().ShouldBe("27821234567");
        sms["content"]!.GetValue<string>().ShouldBe("Your code");
    }

    [Fact]
    public async Task Clickatell_reports_a_rejected_message_as_a_permanent_failure()
    {
        using var http = new FakeHttp(HttpStatusCode.Accepted, """{"messages":[{"accepted":false,"error":{"code":"114","description":"Cannot route message"}}]}""");
        var provider = new ClickatellSmsProvider(http, "clickatell", new ClickatellOptions { ApiKey = "k" });

        var result = await provider.SendAsync(Sms(), CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.IsRetryable.ShouldBeFalse();
        result.Error!.ShouldContain("Cannot route message");
    }

    [Fact]
    public async Task BulkSms_posts_json_with_token_basic_auth()
    {
        using var http = new FakeHttp(HttpStatusCode.Created, """[{"id":"bulk-7","to":"+27821234567"}]""");
        var provider = new BulkSmsProvider(http, "bulksms", new BulkSmsOptions { TokenId = "id", TokenSecret = "sec", From = "Acme" });

        (await provider.SendAsync(Sms(), CancellationToken.None)).ShouldBe(ProviderResult.Sent("bulk-7"));
        http.Request.RequestUri.ShouldBe(new Uri("https://api.bulksms.com/v1/messages"));
        Encoding.UTF8.GetString(Convert.FromBase64String(http.Request.Headers.Authorization!.Parameter!)).ShouldBe("id:sec");
        http.Json["to"]!.GetValue<string>().ShouldBe("+27821234567");
        http.Json["from"]!.GetValue<string>().ShouldBe("Acme");
    }

    [Fact]
    public async Task Meta_whatsapp_sends_a_template_message_to_the_cloud_api()
    {
        using var http = new FakeHttp(HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.ABC"}]}""");
        var provider = new MetaWhatsAppProvider(http, "meta", new MetaWhatsAppOptions { PhoneNumberId = "10999", AccessToken = "EAAG", ApiVersion = "v22.0" });

        (await provider.SendAsync(WhatsApp(), CancellationToken.None)).ShouldBe(ProviderResult.Sent("wamid.ABC"));
        http.Request.RequestUri.ShouldBe(new Uri("https://graph.facebook.com/v22.0/10999/messages"));
        http.Request.Headers.Authorization!.ToString().ShouldBe("Bearer EAAG");
        var body = http.Json;
        body["messaging_product"]!.GetValue<string>().ShouldBe("whatsapp");
        body["to"]!.GetValue<string>().ShouldBe("27821234567");
        body["type"]!.GetValue<string>().ShouldBe("template");
        body["template"]!["name"]!.GetValue<string>().ShouldBe("general_reminder");
        body["template"]!["language"]!["code"]!.GetValue<string>().ShouldBe("en");
        body["template"]!["components"]![0]!["parameters"]!.AsArray().Select(p => p!["text"]!.GetValue<string>()).ShouldBe(["Ann", "Pay rent", "Due Friday"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task Only_transient_http_errors_are_retryable(HttpStatusCode status, bool retryable)
    {
        using var http = new FakeHttp(status, """{"message":"nope"}""");
        var provider = new BulkSmsProvider(http, "bulksms", new BulkSmsOptions { TokenId = "id", TokenSecret = "sec" });

        var result = await provider.SendAsync(Sms(), CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.IsRetryable.ShouldBe(retryable);
        result.Error!.ShouldContain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Configured_providers_fail_fast_on_missing_settings_and_unknown_types()
    {
        static NotificationProviderRegistry Build(Dictionary<string, string?> values)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection().AddLogging().AddHttpClient().BuildServiceProvider();
            return new NotificationProviderRegistry(configuration, services, []);
        }

        Should.Throw<InvalidOperationException>(() => Build(new(StringComparer.Ordinal) { ["Notifications:Providers:sms:Type"] = "Clickatell" }))
            .Message.ShouldContain("Notifications:Providers:sms:ApiKey");
        Should.Throw<InvalidOperationException>(() => Build(new(StringComparer.Ordinal) { ["Notifications:Providers:x:Type"] = "Carrier pigeon" }))
            .Message.ShouldContain("Known types");

        var registry = Build(new(StringComparer.Ordinal)
        {
            ["Notifications:Providers:acme-sms:Type"] = "TwilioSms",
            ["Notifications:Providers:acme-sms:AccountSid"] = "AC1",
            ["Notifications:Providers:acme-sms:AuthToken"] = "t",
            ["Notifications:Providers:acme-sms:From"] = "+1",
        });
        registry.Find("acme-sms", NotificationChannel.Sms).ShouldBeOfType<TwilioSmsProvider>();
        registry.Find("acme-sms", NotificationChannel.Email).ShouldBeNull();
        registry.Find("log", NotificationChannel.WhatsApp).ShouldBeOfType<LogNotificationProvider>();
    }

    [Fact]
    public async Task The_log_provider_hides_bodies_unless_asked()
    {
        var logger = new ListLogger();
        var provider = new LogNotificationProvider("log", NotificationChannel.Email, includeBody: false, logger);

        await provider.SendAsync(Email(), CancellationToken.None);

        logger.Lines.ShouldHaveSingleItem().ShouldContain("(body not logged)");
        logger.Lines.Single().ShouldNotContain("Text body");
    }

    private static OutgoingMessage Email() =>
        new(MessageId, NotificationChannel.Email, "ann@example.com", "Ann", "Hello", "Text body", "<p>Html body</p>", null, []);

    private static OutgoingMessage Sms() =>
        new(MessageId, NotificationChannel.Sms, "+27821234567", "Ann", null, "Your code", null, null, []);

    private static OutgoingMessage WhatsApp() =>
        new(MessageId, NotificationChannel.WhatsApp, "+27821234567", "Ann", null, null, null, Template, ["Ann", "Pay rent", "Due Friday"]);

    private sealed class FakeHttp(HttpStatusCode status, string body, params (string Name, string Value)[] headers) : IHttpClientFactory, IDisposable
    {
        private readonly Handler _handler = new(status, body, headers);

        public HttpRequestMessage Request => _handler.Request!;

        public int Calls => _handler.Calls;

        public JsonNode Json => JsonNode.Parse(_handler.Body!)!;

        public Dictionary<string, string> Form =>
            _handler.Body!.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')), StringComparer.Ordinal);

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

        public void Dispose() => _handler.Dispose();

        private sealed class Handler(HttpStatusCode status, string body, (string Name, string Value)[] headers) : HttpMessageHandler
        {
            public HttpRequestMessage? Request { get; private set; }

            public string? Body { get; private set; }

            public int Calls { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                Request = request;
                Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
                foreach (var (name, value) in headers)
                {
                    response.Headers.Add(name, value);
                }

                return response;
            }
        }
    }

    private sealed class ListLogger : ILogger<LogNotificationProvider>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
