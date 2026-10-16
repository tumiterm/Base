namespace Base.Domain.Common;

/// <summary>
/// Data that belongs to exactly one tenant. Stamped on insert, protected against cross-tenant writes and
/// isolated by the <c>Tenant</c> named query filter. In single-tenant mode everything belongs to the default tenant.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
