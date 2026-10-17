using Base.Domain.Auditing;
using Base.Domain.Common;

namespace Base.Domain.Settings;

/// <summary>
/// A stored value for a catalogued setting. <see cref="TenantId"/> is null for platform-scoped settings.
/// The definition (type, default, limits) lives in code, in the Application layer's <c>SettingCatalog</c>.
/// </summary>
public sealed class ApplicationSetting : EntityBase
{
    public const int CodeMaxLength = 150;
    public const int ValueMaxLength = 4000;

    private ApplicationSetting()
    {
    }

    public ApplicationSetting(string code, string? value, Guid? tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Value = value;
        TenantId = tenantId;
    }

    public string Code { get; private set; } = string.Empty;

    public string? Value { get; private set; }

    /// <summary>
    /// For secret settings only: the value encrypted with ASP.NET Core Data Protection. <see cref="Value"/> stays
    /// empty, so a secret never appears in plain text in the database, the cache or the audit trail.
    /// </summary>
    [AuditRedacted]
    public string? ProtectedValue { get; private set; }

    public Guid? TenantId { get; private set; }

    /// <summary>True once an administrator has changed the value; seeders never overwrite such values.</summary>
    public bool IsOverridden { get; private set; }

    public void ChangeValue(string? value)
    {
        Value = value;
        IsOverridden = true;
    }

    /// <summary>Stores an encrypted secret (null clears it).</summary>
    public void ChangeProtectedValue(string? protectedValue)
    {
        Value = null;
        ProtectedValue = protectedValue;
        IsOverridden = true;
    }

    /// <summary>
    /// Undoes an administrator's change: a platform row goes back to the catalogue default, and a tenant row stops
    /// overriding, so the tenant inherits the platform value again.
    /// </summary>
    public void Revert(string? defaultValue)
    {
        Value = defaultValue;
        ProtectedValue = null;
        IsOverridden = false;
    }

    /// <summary>Used by the seeder only: refreshes a value that no administrator has touched.</summary>
    public void ResetToDefault(string? defaultValue)
    {
        if (!IsOverridden)
        {
            Value = defaultValue;
        }
    }
}
