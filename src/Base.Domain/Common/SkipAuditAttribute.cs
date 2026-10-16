namespace Base.Domain.Common;

/// <summary>
/// Excludes an entity from audit stamping, soft-delete handling, Id generation and the entity conventions.
/// Use only for tables whose schema the application does not own.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class SkipAuditAttribute : Attribute;
