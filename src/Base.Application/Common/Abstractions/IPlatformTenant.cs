namespace Base.Application.Common.Abstractions;

/// <summary>
/// The tenant that runs the platform: the default tenant. Its users can hold platform permissions (tenants, plans,
/// roles); every other tenant's users can't. It has no subscription, so nothing in it is limited.
/// </summary>
public interface IPlatformTenant
{
    Task<Guid> GetIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the current tenant is the platform tenant.</summary>
    Task<bool> IsCurrentAsync(CancellationToken cancellationToken = default);
}
