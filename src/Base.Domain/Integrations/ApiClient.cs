using Base.Domain.Auditing;
using Base.Domain.Common;

namespace Base.Domain.Integrations;

/// <summary>Raised once per key, two weeks before it expires, so its creator can rotate it in time.</summary>
public sealed record ApiKeyExpiring(Guid TenantId, Guid ClientId, Guid CreatedByUserId, string Name, DateTimeOffset ExpiresAt) : IDomainEvent;

/// <summary>
/// Another system that calls the API with a key: a billing platform, an integration, a script. It acts with the
/// permissions it was given (never more than whoever created it held), within one tenant. Only a hash of the key is
/// stored; the key itself is shown once, when it is created or rotated.
/// </summary>
public sealed class ApiClient : EntityBase, ITenantOwned
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int PrefixLength = 8;
    public const int HashLength = 64;

    private List<string> _permissions = [];

    private ApiClient()
    {
    }

    public ApiClient(string name, string keyPrefix, string keyHash, IEnumerable<string> permissions, DateTimeOffset expiresAt, Guid createdByUserId)
    {
        Id = SequentialGuid.Next();
        CreatedByUserId = createdByUserId;
        Change(name, null, permissions, requestsPerMinute: null);
        SetKey(keyPrefix, keyHash, expiresAt);
    }

    public Guid TenantId { get; set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>The public part of the key (<c>bk_{prefix}_…</c>): finds the client and identifies it in logs.</summary>
    public string KeyPrefix { get; private set; } = string.Empty;

    /// <summary>SHA-256 of the whole key, hex. The key is high-entropy, so a fast hash is enough.</summary>
    [AuditRedacted]
    public string KeyHash { get; private set; } = string.Empty;

    public IReadOnlyList<string> Permissions => _permissions;

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Null uses the platform default.</summary>
    public int? RequestsPerMinute { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset? RotatedAt { get; private set; }

    [NotAudited]
    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>When the creator was told the key expires soon, so they're told once per key.</summary>
    [NotAudited]
    public DateTimeOffset? ExpiryNoticeSentAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Change(string name, string? description, IEnumerable<string> permissions, int? requestsPerMinute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(permissions);
        if (requestsPerMinute is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestsPerMinute), "A rate limit is at least one request a minute.");
        }

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        _permissions = permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        RequestsPerMinute = requestsPerMinute;
    }

    /// <summary>Replaces the key; the old one stops working at once.</summary>
    public void Rotate(string keyPrefix, string keyHash, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        EnsureNotRevoked();
        SetKey(keyPrefix, keyHash, expiresAt);
        RotatedAt = now;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void MarkUsed(DateTimeOffset now) => LastUsedAt = now;

    public void MarkExpiryNoticeSent(DateTimeOffset now)
    {
        ExpiryNoticeSentAt = now;
        Raise(new ApiKeyExpiring(TenantId, Id, CreatedByUserId, Name, ExpiresAt));
    }

    private void SetKey(string keyPrefix, string keyHash, DateTimeOffset expiresAt)
    {
        if (keyPrefix is not { Length: PrefixLength } || keyHash is not { Length: HashLength })
        {
            throw new ArgumentException("A key needs an 8-character prefix and a SHA-256 hash.", nameof(keyPrefix));
        }

        KeyPrefix = keyPrefix;
        KeyHash = keyHash;
        ExpiresAt = expiresAt;
        ExpiryNoticeSentAt = null;
    }

    private void EnsureNotRevoked()
    {
        if (IsRevoked)
        {
            throw new InvalidOperationException("A revoked client can't be given a new key.");
        }
    }
}
