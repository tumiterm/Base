using Base.Application.Auditing;
using Base.Application.Common.Records;
using Base.Application.Security;
using Base.Infrastructure.Auditing;
using Base.Web.Security;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

[Route("admin/audit")]
[HasPermission(PermissionCodes.Audit.View)]
public sealed class AuditAdminController(IAuditTrail audit, AuditedEntityTypes entityTypes, IRecordLabels records) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] AuditFilterViewModel filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Dates are picked as calendar days; "to" includes the whole day.
        var query = new AuditQuery(
            filter.EntityType,
            filter.EntityId,
            filter.UserId,
            filter.From is { } from ? new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            filter.To is { } to ? new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            filter.Page,
            50);

        var entries = await audit.SearchAsync(query, cancellationToken);
        var owners = entries.Items.Select(e => AuditRecordOwners.OwnerOf(e.EntityType, e.EntityId)).Select(o => new RecordRef(o.EntityType, o.EntityId)).ToList();
        if (filter.EntityType is not null && filter.EntityId is not null)
        {
            owners.Add(new RecordRef(filter.EntityType, filter.EntityId));
        }

        if (filter.UserId is { } userId)
        {
            owners.Add(new RecordRef("User", userId.ToString()));
        }

        var labels = await records.ResolveAsync(owners, cancellationToken);
        return View(new AuditListViewModel(entries, filter, entityTypes.Names, labels));
    }
}
