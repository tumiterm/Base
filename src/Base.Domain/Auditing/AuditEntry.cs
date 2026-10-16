using Base.Domain.Common;

namespace Base.Domain.Auditing;

public enum AuditAction
{
    Created = 0,
    Updated = 1,

    /// <summary>Soft-deleted (retired); the row still exists.</summary>
    Deleted = 2,

    /// <summary>A soft-deleted row was brought back.</summary>
    Restored = 3,

    /// <summary>Physically removed from the database.</summary>
    Purged = 4,
}

/// <summary>
/// One recorded change to one row: who, what, when and the before/after values. Written by the persistence layer
/// in the same transaction as the change; append-only.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="EntityBase"/>: an audit record is never edited, soft-deleted or itself audited.
/// <see cref="TenantId"/> is null for platform-level changes (e.g. tenants, platform settings).
/// </remarks>
public sealed class AuditEntry
{
    public const int EntityTypeMaxLength = 200;
    public const int EntityIdMaxLength = 100;
    public const int UserNameMaxLength = 256;
    public const int TraceIdMaxLength = 64;

    private AuditEntry()
    {
    }

    public AuditEntry(
        Guid? tenantId,
        string entityType,
        string entityId,
        AuditAction action,
        string changes,
        Guid? userId,
        string userName,
        string? traceId,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        Id = SequentialGuid.Next();
        TenantId = tenantId;
        EntityType = entityType;
        EntityId = entityId;
        Action = action;
        Changes = changes;
        UserId = userId;
        UserName = userName;
        TraceId = traceId;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    public Guid? TenantId { get; private set; }

    /// <summary>Short type name, e.g. <c>StoredFile</c>, <c>ApplicationUser</c>, <c>UserRole</c>.</summary>
    public string EntityType { get; private set; } = string.Empty;

    /// <summary>The row's key as text; composite keys are joined with <c>|</c>.</summary>
    public string EntityId { get; private set; } = string.Empty;

    public AuditAction Action { get; private set; }

    /// <summary>JSON array of <c>{ "property", "old", "new" }</c>. Redacted properties carry no values.</summary>
    public string Changes { get; private set; } = "[]";

    public Guid? UserId { get; private set; }

    public string UserName { get; private set; } = string.Empty;

    /// <summary>The request's trace id: joins the audit record to its log lines and traces.</summary>
    public string? TraceId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}
