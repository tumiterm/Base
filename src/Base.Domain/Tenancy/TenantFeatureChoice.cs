using Base.Domain.Common;

namespace Base.Domain.Tenancy;

/// <summary>
/// A tenant's own decision about one release feature: trying it before adopting its release, or keeping the old
/// behaviour of one that is on by default. Without a choice, the feature follows the tenant's adopted release.
/// </summary>
public sealed class TenantFeatureChoice : EntityBase, ITenantOwned
{
    public const int FeatureCodeMaxLength = 100;

    private TenantFeatureChoice()
    {
    }

    public TenantFeatureChoice(string featureCode, bool isOn, DateTimeOffset chosenAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureCode);
        Id = SequentialGuid.Next();
        FeatureCode = featureCode;
        Change(isOn, chosenAt);
    }

    public Guid TenantId { get; set; }

    public string FeatureCode { get; private set; } = string.Empty;

    public bool IsOn { get; private set; }

    public DateTimeOffset ChosenAt { get; private set; }

    public void Change(bool isOn, DateTimeOffset chosenAt)
    {
        IsOn = isOn;
        ChosenAt = chosenAt;
    }
}
