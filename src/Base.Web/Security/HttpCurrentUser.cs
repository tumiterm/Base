using System.Security.Claims;
using Base.Application.Common.Abstractions;
using Base.Application.Security;
using Base.Infrastructure.Identity;

namespace Base.Web.Security;

/// <summary><see cref="ICurrentUser"/> from the authenticated principal's claims. Falls back to "System" outside a request.</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string AuditName =>
        IsAuthenticated
            ? Principal?.FindFirstValue(AppClaimTypes.DisplayName) ?? Principal?.Identity?.Name ?? ICurrentUser.SystemName
            : ICurrentUser.SystemName;

    public bool HasPermission(string permission) =>
        IsAuthenticated && Principal!.HasClaim(PermissionCatalog.ClaimType, permission);
}
