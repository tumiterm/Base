using Base.Application.Auditing;
using Base.Application.Common.Persistence;
using Base.Application.Security;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

[ApiController]
[Route("api/audit")]
[HasPermission(PermissionCodes.Audit.View)]
public sealed class AuditController(IAuditTrail audit) : ControllerBase
{
    /// <summary>The current tenant's audit trail, newest first. <paramref name="to"/> is exclusive.</summary>
    [HttpGet]
    public Task<PagedResult<AuditRecord>> Search(
        [FromQuery] string? entityType,
        [FromQuery] string? entityId,
        [FromQuery] Guid? userId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        audit.SearchAsync(new AuditQuery(entityType, entityId, userId, from, to, page, pageSize), cancellationToken);
}
