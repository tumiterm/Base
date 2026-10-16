namespace Base.Application.Common.Abstractions;

/// <summary>
/// The tenant the current request or job runs for. Scoped. Resolution fails closed: when no tenant has been
/// resolved, tenant-owned queries return nothing and tenant-owned writes are rejected.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }

    string? TenantCode { get; }

    bool IsResolved { get; }

    /// <summary>True inside <see cref="BeginSystemScope"/>: tenant filtering is lifted for platform-level work.</summary>
    bool IsSystemScope { get; }

    void SetTenant(Guid tenantId, string? tenantCode);

    /// <summary>Lifts tenant isolation until disposed. For platform jobs and seeders only; every use should be reviewable.</summary>
    IDisposable BeginSystemScope();
}
