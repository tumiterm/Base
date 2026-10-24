using System.Collections.Frozen;
using System.Globalization;

namespace Base.Application.Subscriptions;

/// <summary>
/// Features a plan can switch on, as constants. A feature = a constant here + an <see cref="EntitlementCatalog"/> entry
/// (its module, suggested tier and the version it arrived in) + something that enforces it (<c>[RequiresFeature]</c>,
/// <c>asp-feature</c> or <see cref="ITenantEntitlements"/>). Never declare one that nothing checks.
/// </summary>
public static class FeatureCodes
{
    public const string Documents = "Feature.Documents";
    public const string DocumentGeneration = "Feature.DocumentGeneration";
    public const string DataExchange = "Feature.DataExchange";
    public const string CustomBranding = "Feature.CustomBranding";
    public const string Api = "Feature.Api";
    public const string Messaging = "Feature.Messaging";
    public const string BulkMessaging = "Feature.BulkMessaging";
}

/// <summary>Limits a plan sets. Same rule as features: each one is enforced somewhere.</summary>
public static class LimitCodes
{
    public const string Users = "Limit.Users";
    public const string StorageMb = "Limit.StorageMb";

    /// <summary>Messages sent from Messages each calendar month (UTC), counted per channel and recipient.</summary>
    public const string MessagesPerMonth = "Limit.MessagesPerMonth";
}

/// <summary>The functional areas plan features belong to, as constants.</summary>
public static class ModuleCodes
{
    public const string Organisation = "Module.Organisation";
    public const string Documents = "Module.Documents";
    public const string DataExchange = "Module.DataExchange";
    public const string Branding = "Module.Branding";
    public const string Integrations = "Module.Integrations";
    public const string Messaging = "Module.Messaging";
}

public enum EntitlementKind
{
    Feature = 0,
    Limit = 1,
}

/// <summary>
/// The developer's suggestion for a new feature. Standard: in every plan. Premium: kept for higher plans. Until the
/// platform decides for a plan, the plan follows the suggestion (Standard on, Premium off).
/// </summary>
public enum FeatureTier
{
    Standard = 0,
    Premium = 1,
}

/// <summary>A piece of functionality (Documents, Branding...) that groups the features and limits plans are built from.</summary>
public sealed record PlanModule(string Code, string Name, string Description, string Icon);

/// <param name="Module">The <see cref="ModuleCodes"/> entry it belongs to.</param>
/// <param name="Since">The version it arrived in (shown as "Added in 1.1").</param>
/// <param name="DefaultValue">For a plan that hasn't decided: a feature's 1/0 (from its tier), a limit's maximum (null = unlimited).</param>
/// <param name="UnitSize">How many units of measured usage one unit of the limit is (bytes per MB for storage).</param>
/// <param name="ResetsMonthly">A limit on what is used up each month (messages), not on what is kept (users, storage).</param>
public sealed record EntitlementDefinition(
    string Code,
    string Name,
    EntitlementKind Kind,
    string Description,
    long? DefaultValue,
    string Module,
    string Since,
    string? Unit = null,
    long UnitSize = 1,
    bool ResetsMonthly = false)
{
    /// <summary>The suggested tier of a feature; null for a limit.</summary>
    public FeatureTier? Tier => Kind == EntitlementKind.Feature ? (DefaultValue == 1 ? FeatureTier.Standard : FeatureTier.Premium) : null;

    public static EntitlementDefinition Feature(string code, string name, string module, FeatureTier tier, string since, string description) =>
        new(code, name, EntitlementKind.Feature, description, tier == FeatureTier.Standard ? 1 : 0, module, since);

    public static EntitlementDefinition Limit(
        string code, string name, string module, long? defaultValue, string since, string description, string? unit = null, long unitSize = 1, bool resetsMonthly = false) =>
        new(code, name, EntitlementKind.Limit, description, defaultValue, module, since, unit, unitSize, resetsMonthly);

    public string Format(long? value) => Kind == EntitlementKind.Feature
        ? (value == 1 ? "Included" : "Not included")
        : value is null ? "Unlimited" : string.Create(CultureInfo.InvariantCulture, $"{value:N0}{(Unit is null ? string.Empty : " " + Unit)}");
}

/// <summary>
/// What plans are built from: modules, and in each one its features and limits. Projects add their own; a new entry
/// shows up on Platform > Plans by itself, marked "New" with its suggested tier until each plan has decided.
/// </summary>
public static class EntitlementCatalog
{
    public static IReadOnlyList<PlanModule> Modules { get; } =
    [
        new(ModuleCodes.Organisation, "Organisation", "Who can sign in.", "users"),
        new(ModuleCodes.Documents, "Documents & files", "Storing, versioning and generating documents.", "file-text"),
        new(ModuleCodes.DataExchange, "Import & export", "Moving lists in and out as Excel or CSV.", "database"),
        new(ModuleCodes.Branding, "Branding", "Making the application look like the organisation's own.", "palette"),
        new(ModuleCodes.Messaging, "Messaging", "Writing to people by email, SMS, WhatsApp and in-app, one at a time or in bulk.", "send"),
        new(ModuleCodes.Integrations, "Integrations", "Connecting other systems.", "key-round"),
    ];

    public static IReadOnlyList<EntitlementDefinition> All { get; } =
    [
        EntitlementDefinition.Limit(LimitCodes.Users, "Users", ModuleCodes.Organisation, 5, "1.0", "User accounts, invited or active. Locked accounts don't count."),
        EntitlementDefinition.Feature(FeatureCodes.Documents, "Documents", ModuleCodes.Documents, FeatureTier.Standard, "1.0", "Upload, version, preview and link documents."),
        EntitlementDefinition.Feature(FeatureCodes.DocumentGeneration, "Documents from templates", ModuleCodes.Documents, FeatureTier.Premium, "1.1", "Generate letters and forms from templates, as PDF or HTML."),
        EntitlementDefinition.Limit(LimitCodes.StorageMb, "File storage", ModuleCodes.Documents, 1024, "1.0", "Total size of stored files.", "MB", 1024L * 1024L),
        EntitlementDefinition.Feature(FeatureCodes.DataExchange, "Import & export", ModuleCodes.DataExchange, FeatureTier.Standard, "1.0", "Excel and CSV exports of lists, and file imports."),
        EntitlementDefinition.Feature(FeatureCodes.CustomBranding, "Custom branding", ModuleCodes.Branding, FeatureTier.Premium, "1.0", "Own accent colour, logo and default theme."),
        EntitlementDefinition.Feature(FeatureCodes.Messaging, "Send messages", ModuleCodes.Messaging, FeatureTier.Standard, "1.1", "Write to chosen people by email, SMS, WhatsApp or in-app, now or at a set time, with saved templates."),
        EntitlementDefinition.Feature(FeatureCodes.BulkMessaging, "Bulk messages", ModuleCodes.Messaging, FeatureTier.Premium, "1.1", "Send to everyone a filter matches (a department, a role) or to a pasted list."),
        EntitlementDefinition.Limit(LimitCodes.MessagesPerMonth, "Messages per month", ModuleCodes.Messaging, 1000, "1.1", "Messages sent from Messages each month, one per person per channel.", resetsMonthly: true),
        EntitlementDefinition.Feature(FeatureCodes.Api, "API access", ModuleCodes.Integrations, FeatureTier.Premium, "1.0", "Other systems can call the API with API keys."),
    ];

    private static readonly FrozenDictionary<string, EntitlementDefinition> ByCode = All.ToFrozenDictionary(e => e.Code, StringComparer.Ordinal);

    /// <summary>The module's features and limits, in catalogue order.</summary>
    public static IEnumerable<EntitlementDefinition> In(string moduleCode) => All.Where(e => string.Equals(e.Module, moduleCode, StringComparison.Ordinal));

    public static IEnumerable<EntitlementDefinition> Features => All.Where(e => e.Kind == EntitlementKind.Feature);

    public static IEnumerable<EntitlementDefinition> Limits => All.Where(e => e.Kind == EntitlementKind.Limit);

    public static EntitlementDefinition? Find(string code) => ByCode.GetValueOrDefault(code);

    public static EntitlementDefinition Get(string code) =>
        Find(code) ?? throw new KeyNotFoundException($"Entitlement '{code}' is not in the EntitlementCatalog.");
}
