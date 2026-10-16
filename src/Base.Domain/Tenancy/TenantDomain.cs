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

    public static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();
}
