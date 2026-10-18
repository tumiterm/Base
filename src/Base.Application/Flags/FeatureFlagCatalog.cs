using System.Collections.Frozen;

namespace Base.Application.Flags;

public enum FeatureFlagKind
{
    /// <summary>Unfinished or risky code, shipped switched off and opened up gradually. Temporary: has a remove-by date.</summary>
    Release = 0,

    /// <summary>Lets the platform stop something at once (an integration, a costly job) without a deploy. Permanent.</summary>
    KillSwitch = 1,
}

/// <summary>Codes of feature flags. A flag = a constant here + a <see cref="FeatureFlagCatalog"/> entry + a gate.</summary>
public static class FeatureFlagCodes
{
    /// <summary>Kill switch: when off, no bulk or scheduled message starts sending (they wait, and go when it is on again).</summary>
    public const string MessageSending = "Flag.MessageSending";
}

/// <param name="Owner">Who to ask about it (a team or person), shown on the flags screen.</param>
/// <param name="DefaultOn">Its state until someone changes it: release flags start off, kill switches on.</param>
/// <param name="RemoveBy">Release flags: when it should be gone (on for everyone, then the flag and old code deleted).</param>
public sealed record FeatureFlagDefinition(
    string Code,
    string Name,
    string Description,
    FeatureFlagKind Kind,
    string Owner,
    bool DefaultOn,
    DateOnly? RemoveBy = null);

/// <summary>
/// The flags in this build. A project adds a release flag for work it ships switched off, and removes it once it is on
/// everywhere; kill switches stay.
/// </summary>
public static class FeatureFlagCatalog
{
    public static IReadOnlyList<FeatureFlagDefinition> All { get; } =
    [
        new(FeatureFlagCodes.MessageSending, "Message sending", "Turn off to stop messages written in Messages from going out, for example while a provider misbehaves. Messages wait and go once it is on again.",
            FeatureFlagKind.KillSwitch, "Platform team", DefaultOn: true),
    ];
}

/// <summary>The flags the application knows; injectable so tests can bring their own.</summary>
public interface IFeatureFlagCatalog
{
    IReadOnlyList<FeatureFlagDefinition> Flags { get; }

    FeatureFlagDefinition? Find(string code);
}

internal sealed class DefaultFeatureFlagCatalog : IFeatureFlagCatalog
{
    private readonly FrozenDictionary<string, FeatureFlagDefinition> _byCode = FeatureFlagCatalog.All.ToFrozenDictionary(f => f.Code, StringComparer.Ordinal);

    public IReadOnlyList<FeatureFlagDefinition> Flags => FeatureFlagCatalog.All;

    public FeatureFlagDefinition? Find(string code) => _byCode.GetValueOrDefault(code);
}
