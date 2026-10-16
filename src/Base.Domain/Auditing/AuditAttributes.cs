namespace Base.Domain.Auditing;

/// <summary>Excludes an entity type, or a single property, from the audit trail.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class NotAuditedAttribute : Attribute;

/// <summary>
/// Records that the property changed, but never its values. Use for secrets and personal data that must not be
/// copied into the audit table (ID numbers, medical details, tokens).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class AuditRedactedAttribute : Attribute;
