using Base.Application.Common.Abstractions;

namespace Base.Infrastructure.Tenancy;

/// <summary>The platform tenant is the configured default tenant (<c>Tenancy:DefaultTenantCode</c>).</summary>
internal sealed class PlatformTenant(ITenantResolver resolver, ITenantContext tenant) : IPlatformTenant
{
    public async Task<Guid> GetIdAsync(CancellationToken cancellationToken = default) =>
        (await resolver.GetDefaultAsync(cancellationToken))?.Id ?? Guid.Empty;

    public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken = default) =>
        tenant.IsResolved && tenant.TenantId == await GetIdAsync(cancellationToken);
}
