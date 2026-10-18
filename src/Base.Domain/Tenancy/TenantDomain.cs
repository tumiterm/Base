using Base.Domain.Common;

namespace Base.Domain.Tenancy;

/// <summary>A host name that resolves to a tenant. Only verified domains are used for resolution.</summary>
public sealed class TenantDomain : EntityBase
{
    public const int HostMaxLength = 253;

    private TenantDomain()
    {
    }

    internal TenantDomain(Guid tenantId, string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        TenantId = tenantId;
        Host = Normalize(host);
    }

    public Guid TenantId { get; private set; }

    public string Host { get; private set; } = string.Empty;

    public bool IsVerified { get; private set; }

    public void MarkVerified() => IsVerified = true;

    /// <summary>
    /// The bare host name, lower case. A pasted address works too: <c>https://MTN.dev.localhost:7262/login</c> becomes
    /// <c>mtn.dev.localhost</c> (scheme, port and path are dropped; tenants are told apart by host name alone).
    /// </summary>
    public static string Normalize(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var value = host.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        var end = value.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
        {
            value = value[..end];
        }

        var port = value.LastIndexOf(':');
        if (port >= 0 && value[(port + 1)..].All(char.IsAsciiDigit))
        {
            value = value[..port];
        }

        return value.TrimEnd('.').ToLowerInvariant();
    }
}
