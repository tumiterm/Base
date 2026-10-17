using System.Collections.Frozen;
using Base.Application.Settings;

namespace Base.Web.Formatting;

/// <summary>How the settings screen presents categories and the few settings that are edited on other pages.</summary>
public static class SettingsDisplay
{
    private static readonly FrozenDictionary<string, (string Icon, string Description, int Order)> Categories =
        new Dictionary<string, (string Icon, string Description, int Order)>(StringComparer.Ordinal)
        {
            ["General"] = ("settings", "Name, address, time zone and announcements.", 0),
            ["Email"] = ("mail", "How the application sends email: the SMTP server and the sender.", 1),
            ["Security"] = ("shield-check", "Sign-in and password options.", 2),
            ["Branding"] = ("palette", "Colours, theme and logo.", 3),
            ["Documents"] = ("file-text", "How documents are shown.", 4),
            ["Files"] = ("file-up", "Upload limits, file types and where files are stored.", 5),
            ["Notifications"] = ("bell", "Providers per channel, phone numbers and error alerts.", 6),
            ["Data exchange"] = ("database", "Export and import limits.", 7),
            ["API"] = ("key-round", "Rate limits, key lifetimes and retries.", 8),
            ["Subscriptions"] = ("credit-card", "Grace periods and trial reminders.", 9),
            ["Audit"] = ("history", "How long the audit trail is kept.", 10),
            ["System"] = ("server", "Housekeeping.", 11),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string Icon(string category) => Categories.TryGetValue(category, out var c) ? c.Icon : "sliders-horizontal";

    public static string Describe(string category) => Categories.TryGetValue(category, out var c) ? c.Description : string.Empty;

    public static int Order(string category) => Categories.TryGetValue(category, out var c) ? c.Order : 100;

    /// <summary>Categories with their own screen (they keep caches and previews in step); shown read-only here.</summary>
    public static (string Controller, string Action, string Label)? ManagedOn(string category) =>
        string.Equals(category, "Branding", StringComparison.Ordinal) ? ("Appearance", "Index", "Appearance") : null;

    /// <summary>The value as people read it: "On", "Off", "none" when empty.</summary>
    public static string Format(SettingDefinition definition, string? value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        return definition.DataType == SettingDataType.Boolean
            ? (bool.TryParse(value, out var on) && on ? "On" : "Off")
            : value;
    }
}
