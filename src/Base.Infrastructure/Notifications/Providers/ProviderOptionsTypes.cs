namespace Base.Infrastructure.Notifications.Providers;

public sealed class SmtpProviderOptions : IValidatableProviderOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;

    public IEnumerable<string> MissingSettings()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            yield return nameof(Host);
        }

        if (string.IsNullOrWhiteSpace(FromAddress))
        {
            yield return nameof(FromAddress);
        }
    }
}

public sealed class SendGridOptions : IValidatableProviderOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;

    public Uri BaseUrl { get; set; } = new("https://api.sendgrid.com/");

    public IEnumerable<string> MissingSettings()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return nameof(ApiKey);
        }

        if (string.IsNullOrWhiteSpace(FromAddress))
        {
            yield return nameof(FromAddress);
        }
    }
}

/// <summary>Twilio SMS and WhatsApp. Use an API key (recommended) or the account auth token.</summary>
public sealed class TwilioOptions : IValidatableProviderOptions
{
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>API key SID (SK...), or empty to authenticate with <see cref="AccountSid"/>.</summary>
    public string? ApiKeySid { get; set; }

    /// <summary>The API key secret, or the account auth token.</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>Sender number in E.164 (+1415...). Not needed when <see cref="MessagingServiceSid"/> is set.</summary>
    public string? From { get; set; }

    public string? MessagingServiceSid { get; set; }

    /// <summary>WhatsApp: approved template name → Twilio Content SID (HX...).</summary>
    public IDictionary<string, string> ContentSids { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public Uri BaseUrl { get; set; } = new("https://api.twilio.com/");

    public IEnumerable<string> MissingSettings()
    {
        if (string.IsNullOrWhiteSpace(AccountSid))
        {
            yield return nameof(AccountSid);
        }

        if (string.IsNullOrWhiteSpace(AuthToken))
        {
            yield return nameof(AuthToken);
        }

        if (string.IsNullOrWhiteSpace(From) && string.IsNullOrWhiteSpace(MessagingServiceSid))
        {
            yield return $"{nameof(From)} or {nameof(MessagingServiceSid)}";
        }
    }
}

/// <summary>Clickatell Platform (One API) SMS.</summary>
public sealed class ClickatellOptions : IValidatableProviderOptions
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Optional two-way number or sender id registered with Clickatell.</summary>
    public string? From { get; set; }

    public Uri BaseUrl { get; set; } = new("https://platform.clickatell.com/");

    public IEnumerable<string> MissingSettings()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return nameof(ApiKey);
        }
    }
}

/// <summary>BulkSMS (bulksms.com) JSON REST API, authenticated with an API token id and secret.</summary>
public sealed class BulkSmsOptions : IValidatableProviderOptions
{
    public string TokenId { get; set; } = string.Empty;

    public string TokenSecret { get; set; } = string.Empty;

    public string? From { get; set; }

    public Uri BaseUrl { get; set; } = new("https://api.bulksms.com/");

    public IEnumerable<string> MissingSettings()
    {
        if (string.IsNullOrWhiteSpace(TokenId))
        {
            yield return nameof(TokenId);
        }

        if (string.IsNullOrWhiteSpace(TokenSecret))
        {
            yield return nameof(TokenSecret);
        }
    }
}

/// <summary>Meta WhatsApp Business Cloud API. Only approved templates are sent.</summary>
public sealed class MetaWhatsAppOptions : IValidatableProviderOptions
{
    public string PhoneNumberId { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "v22.0";

    public Uri BaseUrl { get; set; } = new("https://graph.facebook.com/");

    public IEnumerable<string> MissingSettings()
    {
        if (string.IsNullOrWhiteSpace(PhoneNumberId))
        {
            yield return nameof(PhoneNumberId);
        }

        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            yield return nameof(AccessToken);
        }
    }
}
