using Base.Domain.Common;

namespace Base.Domain.Flags;

public enum FeatureFlagMode
{
    Off = 0,

    /// <summary>On for every organisation.</summary>
    Everyone = 1,

    /// <summary>On for the chosen organisations, a share of all organisations, and/or the platform's own staff.</summary>
    Targeted = 2,
}

/// <summary>
/// Who a feature flag is on for. Platform data, one row per flag that has ever been changed; a flag without a row uses
/// its catalogue default. Changes are audited like any other entity.
/// </summary>
public sealed class FeatureFlagState : EntityBase
{
    public const int CodeMaxLength = 100;

    private List<Guid> _tenantIds = [];

    private FeatureFlagState()
    {
    }

    public FeatureFlagState(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Id = SequentialGuid.Next();
        Code = code;
    }

    public string Code { get; private set; } = string.Empty;

    public FeatureFlagMode Mode { get; private set; }

    /// <summary>Targeted: the share of organisations (0–100) it is on for, always the same ones as it grows.</summary>
    public int Percentage { get; private set; }

    /// <summary>Targeted: organisations it is on for whatever the percentage.</summary>
    public IReadOnlyList<Guid> TenantIds => _tenantIds;

    /// <summary>Targeted: on for the platform organisation, so its staff can try it first.</summary>
    public bool IncludePlatform { get; private set; }

    public void Set(FeatureFlagMode mode, int percentage, IEnumerable<Guid> tenantIds, bool includePlatform)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percentage);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentage, 100);
        ArgumentNullException.ThrowIfNull(tenantIds);
        Mode = mode;
        Percentage = mode == FeatureFlagMode.Targeted ? percentage : 0;
        _tenantIds = mode == FeatureFlagMode.Targeted ? tenantIds.Distinct().ToList() : [];
        IncludePlatform = mode == FeatureFlagMode.Targeted && includePlatform;
    }
}
