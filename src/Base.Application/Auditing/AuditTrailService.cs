using Base.Application.Common.Persistence;
using Base.Domain.Auditing;

namespace Base.Application.Auditing;

/// <summary>Reads the audit trail of the current tenant, newest first.</summary>
public interface IAuditTrail
{
    Task<PagedResult<AuditRecord>> SearchAsync(AuditQuery query, CancellationToken cancellationToken = default);
}

internal sealed class AuditTrailService(IUnitOfWork unitOfWork) : IAuditTrail
{
    public async Task<PagedResult<AuditRecord>> SearchAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entityType = string.IsNullOrWhiteSpace(query.EntityType) ? null : query.EntityType.Trim();
        var entityId = string.IsNullOrWhiteSpace(query.EntityId) ? null : query.EntityId.Trim();

        // Composite keys start with their owner's id ("userId|roleId"), so a user's history includes role assignments.
        var compositePrefix = entityId + "|";

        // The Tenant query filter limits this to the current tenant's entries.
        var page = await unitOfWork.Repository<AuditEntry>().GetPagedAsync(
            new PageRequest(query.Page, query.PageSize),
            e => e,
            q => q.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id),
            e => (entityType == null || e.EntityType == entityType)
                && (entityId == null || e.EntityId == entityId || e.EntityId.StartsWith(compositePrefix))
                && (query.UserId == null || e.UserId == query.UserId)
                && (query.From == null || e.OccurredAt >= query.From)
                && (query.To == null || e.OccurredAt < query.To),
            cancellationToken);

        return new PagedResult<AuditRecord>(
            page.Items.Select(e => new AuditRecord(e.Id, e.OccurredAt, e.EntityType, e.EntityId, e.Action, e.UserName, e.UserId, e.TraceId, AuditChange.Deserialize(e.Changes))).ToList(),
            page.Total,
            page.Page,
            page.PageSize);
    }
}
