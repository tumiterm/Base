using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Base.Application.Notifications;
using Base.Domain.Notifications;

namespace Base.Infrastructure.Notifications.Providers;

/// <summary>Shared HTTP plumbing: one named client, and the retry rule (408, 429 and 5xx are retryable; the rest are not).</summary>
public abstract class HttpNotificationProvider(IHttpClientFactory httpClients, string name) : INotificationProvider
{
    public const string HttpClientName = "notifications";

    public string Name { get; } = name;

    public abstract NotificationChannel Channel { get; }

    public async Task<ProviderResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var request = BuildRequest(message);
        if (request is null)
        {
            return ProviderResult.Failed(MissingConfiguration(message), isRetryable: false);
        }

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var retryable = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                return ProviderResult.Failed($"{(int)response.StatusCode} {response.ReasonPhrase}: {Shorten(body)}", retryable);
            }

            return ReadSuccess(response, body);
        }
        catch (HttpRequestException exception)
        {
            return ProviderResult.Failed(exception.Message, isRetryable: true);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderResult.Failed($"Timed out: {exception.Message}", isRetryable: true);
        }
    }

    /// <summary>Null when the message can't be sent with this configuration (e.g. no Content SID for a template).</summary>
    protected abstract HttpRequestMessage? BuildRequest(OutgoingMessage message);

    protected virtual string MissingConfiguration(OutgoingMessage message) => $"{GetType().Name} can't send this message.";

    /// <summary>Reads the provider's message id; may also turn a 2xx into a failure (e.g. Clickatell's per-message "accepted").</summary>
    protected abstract ProviderResult ReadSuccess(HttpResponseMessage response, string body);

    protected static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    /// <summary>Most APIs want the number without "+": 27821234567.</summary>
    protected static string Digits(string e164) => e164.TrimStart('+');

    protected static JsonNode? Parse(string body)
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Shorten(string body) => body.Length <= 500 ? body : body[..500];
}

/// <summary>SendGrid v3 Mail Send (<c>POST /v3/mail/send</c>).</summary>
public sealed class SendGridEmailProvider(IHttpClientFactory httpClients, string name, SendGridOptions options) : HttpNotificationProvider(httpClients, name)
{
    public override NotificationChannel Channel => NotificationChannel.Email;

    protected override HttpRequestMessage BuildRequest(OutgoingMessage message)
    {
        var content = new List<object>();
        if (!string.IsNullOrEmpty(message.Body))
        {
            content.Add(new { type = "text/plain", value = message.Body });
        }

        if (!string.IsNullOrEmpty(message.HtmlBody))
        {
            content.Add(new { type = "text/html", value = message.HtmlBody });
        }

        return new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl, "v3/mail/send"))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey) },
            Content = JsonContent.Create(new
            {
                personalizations = new[] { new { to = new[] { new { email = message.To, name = message.ToName } } } },
                from = new { email = options.FromAddress, name = options.FromName },
                subject = message.Subject,
                content,
                custom_args = new { message_id = message.MessageId.ToString() },
            }),
        };
    }

    protected override ProviderResult ReadSuccess(HttpResponseMessage response, string body) =>
        ProviderResult.Sent(response.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() : null);
}

/// <summary>Twilio Programmable Messaging (<c>POST /2010-04-01/Accounts/{sid}/Messages.json</c>).</summary>
public abstract class TwilioProviderBase(IHttpClientFactory httpClients, string name, TwilioOptions options) : HttpNotificationProvider(httpClients, name)
{
    protected TwilioOptions Options { get; } = options;

    protected HttpRequestMessage Request(IEnumerable<KeyValuePair<string, string>> form) =>
        new(HttpMethod.Post, new Uri(Options.BaseUrl, $"2010-04-01/Accounts/{Options.AccountSid}/Messages.json"))
        {
            Headers = { Authorization = Basic(string.IsNullOrWhiteSpace(Options.ApiKeySid) ? Options.AccountSid : Options.ApiKeySid, Options.AuthToken) },
            Content = new FormUrlEncodedContent(form),
        };

    protected IEnumerable<KeyValuePair<string, string>> Sender(string prefix) =>
        !string.IsNullOrWhiteSpace(Options.MessagingServiceSid)
            ? [new("MessagingServiceSid", Options.MessagingServiceSid)]
            : [new("From", prefix + Options.From)];

    protected override ProviderResult ReadSuccess(HttpResponseMessage response, string body) =>
        ProviderResult.Sent(Parse(body)?["sid"]?.GetValue<string>());
}

public sealed class TwilioSmsProvider(IHttpClientFactory httpClients, string name, TwilioOptions options) : TwilioProviderBase(httpClients, name, options)
{
    public override NotificationChannel Channel => NotificationChannel.Sms;

    protected override HttpRequestMessage BuildRequest(OutgoingMessage message) =>
        Request([new("To", message.To), new("Body", message.Body ?? string.Empty), .. Sender(string.Empty)]);
}

/// <summary>WhatsApp through Twilio: approved templates are Twilio Content templates, mapped by name to a Content SID.</summary>
public sealed class TwilioWhatsAppProvider(IHttpClientFactory httpClients, string name, TwilioOptions options) : TwilioProviderBase(httpClients, name, options)
{
    public override NotificationChannel Channel => NotificationChannel.WhatsApp;

    protected override HttpRequestMessage? BuildRequest(OutgoingMessage message)
    {
        if (message.WhatsApp is null || !Options.ContentSids.TryGetValue(message.WhatsApp.Name, out var contentSid))
        {
            return null;
        }

        var variables = message.TemplateParameters.Select((value, index) => (Key: (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), value))
            .ToDictionary(p => p.Key, p => p.value, StringComparer.Ordinal);
        return Request([new("To", "whatsapp:" + message.To), new("ContentSid", contentSid), new("ContentVariables", JsonSerializer.Serialize(variables)), .. Sender("whatsapp:")]);
    }

    protected override string MissingConfiguration(OutgoingMessage message) =>
        $"No Twilio Content SID configured for WhatsApp template '{message.WhatsApp?.Name}' (Notifications:Providers:{Name}:ContentSids).";
}

/// <summary>Clickatell Platform One API (<c>POST /v1/message</c>).</summary>
public sealed class ClickatellSmsProvider(IHttpClientFactory httpClients, string name, ClickatellOptions options) : HttpNotificationProvider(httpClients, name)
{
    public override NotificationChannel Channel => NotificationChannel.Sms;

    protected override HttpRequestMessage BuildRequest(OutgoingMessage message)
    {
        var sms = new JsonObject { ["channel"] = "sms", ["to"] = Digits(message.To), ["content"] = message.Body };
        if (!string.IsNullOrWhiteSpace(options.From))
        {
            sms["from"] = options.From;
        }

        return new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl, "v1/message"))
        {
            Headers = { Authorization = new AuthenticationHeaderValue(options.ApiKey) },
            Content = new StringContent(new JsonObject { ["messages"] = new JsonArray(sms) }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    protected override ProviderResult ReadSuccess(HttpResponseMessage response, string body)
    {
        var first = Parse(body)?["messages"]?[0];
        if (first?["accepted"]?.GetValue<bool>() == false)
        {
            return ProviderResult.Failed($"Clickatell rejected the message: {first["error"]?.ToJsonString()}", isRetryable: false);
        }

        return ProviderResult.Sent(first?["apiMessageId"]?.GetValue<string>());
    }
}

/// <summary>BulkSMS JSON REST API (<c>POST /v1/messages</c>).</summary>
public sealed class BulkSmsProvider(IHttpClientFactory httpClients, string name, BulkSmsOptions options) : HttpNotificationProvider(httpClients, name)
{
    public override NotificationChannel Channel => NotificationChannel.Sms;

    protected override HttpRequestMessage BuildRequest(OutgoingMessage message)
    {
        var sms = new JsonObject { ["to"] = message.To, ["body"] = message.Body };
        if (!string.IsNullOrWhiteSpace(options.From))
        {
            sms["from"] = options.From;
        }

        return new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl, "v1/messages"))
        {
            Headers = { Authorization = Basic(options.TokenId, options.TokenSecret) },
            Content = new StringContent(sms.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    protected override ProviderResult ReadSuccess(HttpResponseMessage response, string body) =>
        ProviderResult.Sent(Parse(body)?[0]?["id"]?.GetValue<string>());
}

/// <summary>Meta WhatsApp Cloud API (<c>POST /{version}/{phone-number-id}/messages</c>), approved templates only.</summary>
public sealed class MetaWhatsAppProvider(IHttpClientFactory httpClients, string name, MetaWhatsAppOptions options) : HttpNotificationProvider(httpClients, name)
{
    public override NotificationChannel Channel => NotificationChannel.WhatsApp;

    protected override HttpRequestMessage? BuildRequest(OutgoingMessage message)
    {
        if (message.WhatsApp is null)
        {
            return null;
        }

        var parameters = new JsonArray(message.TemplateParameters.Select(p => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = p }).ToArray());
        var payload = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["to"] = Digits(message.To),
            ["type"] = "template",
            ["template"] = new JsonObject
            {
                ["name"] = message.WhatsApp.Name,
                ["language"] = new JsonObject { ["code"] = message.WhatsApp.Language },
                ["components"] = new JsonArray(new JsonObject { ["type"] = "body", ["parameters"] = parameters }),
            },
        };

        return new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl, $"{options.ApiVersion}/{options.PhoneNumberId}/messages"))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken) },
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    protected override string MissingConfiguration(OutgoingMessage message) => "WhatsApp messages must use an approved template.";

    protected override ProviderResult ReadSuccess(HttpResponseMessage response, string body) =>
        ProviderResult.Sent(Parse(body)?["messages"]?[0]?["id"]?.GetValue<string>());
}
