using System.Security.Claims;
using Base.Application.Common.Abstractions;
using Base.Application.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Identity;

/// <summary>
/// Builds the principal at sign-in and on every security-stamp revalidation, so role/permission changes reach
/// signed-in users within the validation interval without a logout (FO's PrincipalFreshness, built in).
/// Permissions are expanded through <see cref="PermissionCatalog.Expand"/> and stored as claims; platform permissions
/// are dropped for users outside the platform tenant.
/// </summary>
internal sealed class AppUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IPlatformTenant platform,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaimTypes.DisplayName, user.DisplayName));
        identity.AddClaim(new Claim(AppClaimTypes.TenantId, user.TenantId.ToString()));

        // Base adds role claims' permissions as-is; replace them with the expanded set.
        var granted = identity.FindAll(PermissionCatalog.ClaimType).ToList();
        foreach (var claim in granted)
        {
            identity.RemoveClaim(claim);
        }

        // Platform permissions only take effect for the platform tenant's users (roles are shared by every tenant).
        var isPlatformUser = user.TenantId == await platform.GetIdAsync();
        foreach (var permission in PermissionCatalog.EffectiveIn(PermissionCatalog.Expand(granted.Select(c => c.Value)), isPlatformUser))
        {
            identity.AddClaim(new Claim(PermissionCatalog.ClaimType, permission));
        }

        return identity;
    }
}
