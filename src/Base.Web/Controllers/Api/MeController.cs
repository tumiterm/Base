using Base.Application.Common.Abstractions;
using Base.Application.Security;
using Base.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

public sealed record MeResponse(Guid? UserId, string Name, string? Tenant, IReadOnlyList<string> Permissions);

/// <summary>Who am I? Lets a front end show or hide features without guessing.</summary>
[ApiController]
[Route("api/me")]
public sealed class MeController(ICurrentUser currentUser, ITenantContext tenant) : ControllerBase
{
    [HttpGet]
    public MeResponse Get() => new(
        currentUser.UserId,
        User.FindFirst(AppClaimTypes.DisplayName)?.Value ?? currentUser.AuditName,
        tenant.TenantCode,
        User.FindAll(PermissionCatalog.ClaimType).Select(c => c.Value).Order(StringComparer.Ordinal).ToList());
}
