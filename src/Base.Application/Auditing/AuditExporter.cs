using Base.Application.Common.Persistence;
using Base.Application.DataExchange;
using Base.Application.Security;

namespace Base.Application.Auditing;

/// <summary>The audit trail. Filters: <c>entityType</c>, <c>entityId</c>, <c>userId</c>.</summary>
internal sealed class AuditExporter(IAuditTrail audit) : Exporter<AuditRecord>
{
    public override string Code => "audit";

    public override string Name => "Audit trail";

    public override string Permission => PermissionCodes.Audit.View;

    protected override IReadOnlyList<ExportColumn<AuditRecord>> Definition { get; } =
    [
        new("When", a => a.OccurredAt, ColumnType.DateTime),
        new("Record type", a => a.EntityType),
        new("Record id", a => a.EntityId),
        new("Action", a => a.Action),
        new("By", a => a.UserName),
        new("Changes", a => string.Join("; ", a.Changes.Select(c => c.Redacted ? $"{c.Property}: changed" : $"{c.Property}: {c.Old ?? "(empty)"} -> {c.New ?? "(empty)"}"))),
        new("Trace id", a => a.TraceId),
    ];

    protected override Task<PagedResult<AuditRecord>> PageAsync(ExportFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        audit.SearchAsync(new AuditQuery(filter.Text("entityType"), filter.Text("entityId"), filter.Guid("userId"), Page: page, PageSize: pageSize), cancellationToken);
}
