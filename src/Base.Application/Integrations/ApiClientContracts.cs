namespace Base.Application.Integrations;

public enum ApiClientState
{
    Active = 0,
    ExpiringSoon = 1,
    Expired = 2,
    Revoked = 3,
}

public sealed record ApiClientSummary(
    Guid Id,
    string Name,
    string? Description,
    string MaskedKey,
    IReadOnlyList<string> Permissions,
    int? RequestsPerMinute,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset DateCreated,
    string? UserCreated,
    ApiClientState State);

/// <param name="ExpiresInDays">How long the key is valid; at most the <c>Api.KeyLifetimeDays</c> setting.</param>
/// <param name="RequestsPerMinute">Null uses <c>Api.DefaultRequestsPerMinute</c>.</param>
public sealed record SaveApiClientRequest(
    string Name,
    string? Description,
    IReadOnlyList<string> Permissions,
    int ExpiresInDays = 90,
    int? RequestsPerMinute = null);

/// <summary>The only time the key is visible. Store it in the calling system's secret store.</summary>
public sealed record IssuedApiKey(Guid ClientId, string Key, DateTimeOffset ExpiresAt);

/// <summary>A caller authenticated by key: who it is and what it may do.</summary>
public sealed record ApiClientIdentity(Guid Id, string Name, Guid TenantId, IReadOnlyList<string> Permissions, int RequestsPerMinute);
