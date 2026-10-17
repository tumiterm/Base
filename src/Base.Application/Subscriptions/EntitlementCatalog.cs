using System.Collections.Frozen;
using System.Globalization;

namespace Base.Application.Subscriptions;

/// <summary>
/// Features a plan can switch on, as constants. A feature = a constant here + an <see cref="EntitlementCatalog"/> entry
/// + something that enforces it (<c>[RequiresFeature]</c> or <see cref="ITenantEntitlements"/>). Never declare one that
/// nothing checks.
/// </summary>
public static class FeatureCodes
{
    public const string Documents = "Feature.Documents";
    public const string DataExchange = "Feature.DataExchange";
    public const string CustomBranding = "Feature.CustomBranding";
    public const string Api = "Feature.Api";
}

/// <summary>Limits a plan sets. Same rule as features: each one is enforced somewhere.</summary>
public static class LimitCodes
{
    public const string Users = "Limit.Users";
    public const string StorageMb = "Limit.StorageMb";
}

public enum EntitlementKind
{
    Feature = 0,
    Limit = 1,
}

/// <param name="DefaultValue">For a plan with no value set: a feature's 1/0, a limit's maximum (null = unlimited).</param>
/// <param name="UnitSize">How many units of measured usage one unit of the limit is (bytes per MB for storage).</param>
public sealed record EntitlementDefinition(
    string Code,
    string Name,
    EntitlementKind Kind,
    string Description,
    long? DefaultValue,
    string? Unit = null,
    long UnitSize = 1)
{
    public string Format(long? value) => Kind == EntitlementKind.Feature
        ? (value == 1 ? "Included" : "Not included")
        : value is null ? "Unlimited" : string.Create(CultureInfo.InvariantCulture, $"{value:N0}{(Unit is null ? string.Empty : " " + Unit)}");
}

public static class EntitlementCatalog
{
    public static IReadOnlyList<EntitlementDefinition> All { get; } =
    [
        new(FeatureCodes.Documents, "Documents", EntitlementKind.Feature, "Versioned documents, generation from templates.", 1),
        new(FeatureCodes.DataExchange, "Import & export", EntitlementKind.Feature, "Excel and CSV exports of lists, and file imports.", 1),
        new(FeatureCodes.CustomBranding, "Custom branding", EntitlementKind.Feature, "Own accent colour, logo and default theme.", 0),
        new(FeatureCodes.Api, "API access", EntitlementKind.Feature, "Other systems can call the API with API keys.", 0),
        new(LimitCodes.Users, "Users", EntitlementKind.Limit, "User accounts, invited or active.", 5),
        new(LimitCodes.StorageMb, "File storage", EntitlementKind.Limit, "Total size of stored files.", 1024, "MB", 1024L * 1024L),
    ];

    private static readonly FrozenDictionary<string, EntitlementDefinition> ByCode = All.ToFrozenDictionary(e => e.Code, StringComparer.Ordinal);

    public static IEnumerable<EntitlementDefinition> Features => All.Where(e => e.Kind == EntitlementKind.Feature);

    public static IEnumerable<EntitlementDefinition> Limits => All.Where(e => e.Kind == EntitlementKind.Limit);

    public static EntitlementDefinition? Find(string code) => ByCode.GetValueOrDefault(code);

    public static EntitlementDefinition Get(string code) =>
        Find(code) ?? throw new KeyNotFoundException($"Entitlement '{code}' is not in the EntitlementCatalog.");
}
