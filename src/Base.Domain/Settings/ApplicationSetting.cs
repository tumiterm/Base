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

    public Guid? TenantId { get; private set; }

    /// <summary>True once an administrator has changed the value; seeders never overwrite such values.</summary>
    public bool IsOverridden { get; private set; }

    public void ChangeValue(string? value)
    {
        Value = value;
        IsOverridden = true;
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
