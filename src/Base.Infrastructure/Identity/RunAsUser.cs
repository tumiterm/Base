using System.Security.Claims;
using Base.Application.Common.Abstractions;
using Base.Application.Security;
using Microsoft.AspNetCore.Identity;

namespace Base.Infrastructure.Identity;

/// <summary>The user a scope is currently acting as, set by <see cref="RunAsUser"/>. Scoped; null outside run-as.</summary>
internal sealed class CurrentUserOverride
{
    public ICurrentUser? User { get; set; }
}

/// <summary>
/// Wraps whichever <see cref="ICurrentUser"/> the host registered (the HTTP one, or "System" for jobs) so a
/// <see cref="RunAsUser"/> scope can act as someone else. Reads the override on every call.
/// </summary>
internal sealed class OverridableCurrentUser(CurrentUserOverride current, ICurrentUser inner) : ICurrentUser
{
    private ICurrentUser Effective => current.User ?? inner;

    public Guid? UserId => Effective.UserId;

    public string AuditName => Effective.AuditName;

    public bool IsAuthenticated => Effective.IsAuthenticated;

    public bool HasPermission(string permission) => Effective.HasPermission(permission);
}

/// <summary>A user built from a principal made by the same factory as sign-in, so permissions match exactly.</summary>
internal sealed class PrincipalCurrentUser(ClaimsPrincipal principal, Guid userId) : ICurrentUser
{
    public Guid? UserId => userId;

    public string AuditName => principal.FindFirstValue(AppClaimTypes.DisplayName) ?? principal.Identity?.Name ?? ICurrentUser.SystemName;

    public bool IsAuthenticated => true;

    public bool HasPermission(string permission) => principal.HasClaim(PermissionCatalog.ClaimType, permission);
}

internal sealed class RunAsUser(
    UserManager<ApplicationUser> users,
    IUserClaimsPrincipalFactory<ApplicationUser> principals,
    CurrentUserOverride current) : IRunAsUser
{
    public async Task<IDisposable?> BeginAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || await users.IsLockedOutAsync(user))
        {
            return null;
        }

        var previous = current.User;
        current.User = new PrincipalCurrentUser(await principals.CreateAsync(user), userId);
        return new Restore(current, previous);
    }

    private sealed class Restore(CurrentUserOverride current, ICurrentUser? previous) : IDisposable
    {
        public void Dispose() => current.User = previous;
    }
}
