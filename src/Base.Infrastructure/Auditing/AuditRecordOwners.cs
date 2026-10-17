namespace Base.Infrastructure.Auditing;

/// <summary>Maps audit rows to the record people know them by, so the trail can name it.</summary>
public static class AuditRecordOwners
{
    /// <summary>
    /// The record an audit entry belongs to, for naming it: claims and role assignments belong to their user or role.
    /// </summary>
    public static (string EntityType, string EntityId) OwnerOf(string entityType, string entityId) => entityType switch
    {
        "UserClaim" => ("User", entityId),
        "UserRole" => ("User", entityId.Split('|')[0]),
        "RoleClaim" => ("Role", entityId),
        _ => (entityType, entityId),
    };
}
