using Base.Application.Common.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Base.Application.Common;

/// <summary>
/// Runs a piece of work as the platform tenant, in a scope of its own: to read the platform's own settings, logo or
/// wording while serving another tenant (what the platform sends an organisation is in the platform's name).
/// </summary>
internal sealed class PlatformScope(IServiceProvider services, IServiceScopeFactory scopes, IPlatformTenant platform, ITenantContext tenant)
{
    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var platformId = await platform.GetIdAsync(cancellationToken);
        if (tenant.IsResolved && tenant.TenantId == platformId)
        {
            return await work(services);
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(platformId, null);
        return await work(scope.ServiceProvider);
    }
}
