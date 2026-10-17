namespace Base.Infrastructure.Identity;

public static class AppClaimTypes
{
    public const string DisplayName = "display_name";
    public const string TenantId = "tenant_id";

    /// <summary>Set when the caller is an API client (authenticated by key), not a person.</summary>
    public const string ApiClientId = "api_client_id";

    /// <summary>The API client's requests-per-minute limit.</summary>
    public const string RateLimit = "rate_limit";
}
