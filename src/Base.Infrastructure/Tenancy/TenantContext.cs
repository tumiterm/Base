using Base.Application.Common.Abstractions;

namespace Base.Infrastructure.Tenancy;

internal sealed class TenantContext : ITenantContext
{
    private int _systemScopeDepth;

    public Guid TenantId { get; private set; }

    public string? TenantCode { get; private set; }

    public bool IsResolved => TenantId != Guid.Empty;

    public bool IsSystemScope => _systemScopeDepth > 0;

    public void SetTenant(Guid tenantId, string? tenantCode)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        TenantCode = tenantCode;
    }

    public IDisposable BeginSystemScope()
    {
        _systemScopeDepth++;
        return new Scope(this);
    }

    private sealed class Scope(TenantContext owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner._systemScopeDepth--;
            }
        }
    }
}
