using System.Collections.Frozen;

namespace Base.Application.Settings;

/// <summary>
/// The single source of truth for settings. A startup seeder inserts missing rows and refreshes untouched
/// defaults but never overwrites a value an administrator has set.
/// </summary>
public static class SettingCatalog
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(SettingCodes.General.ApplicationName, "Application name", SettingDataType.String, "Base", "General",
            "Shown in the page title, emails and the navigation bar."),
        new(SettingCodes.General.DisplayTimeZone, "Display time zone", SettingDataType.TimeZone, "Africa/Johannesburg", "General",
            "Time zone used to display dates. Data is always stored in UTC."),
        new(SettingCodes.General.PublicBaseUrl, "Public address", SettingDataType.Url, "https://localhost:7262", "General",
            "The address users reach the application at. Used to build links in emails (invites, password resets)."),

        new(SettingCodes.Files.MaxUploadSizeMb, "Maximum upload size (MB)", SettingDataType.Integer, "10", "Files",
            "Largest file a user may upload.", Min: 1, Max: 500),
        new(SettingCodes.Files.AllowedExtensions, "Allowed file types", SettingDataType.StringList, ".pdf,.png,.jpg,.jpeg,.docx,.xlsx", "Files",
            "Comma-separated extensions. Content is also checked against the file signature."),
        new(SettingCodes.Files.DefaultProvider, "Storage provider for new files", SettingDataType.String, "LocalDisk", "Files",
            "Where new uploads are stored. Existing files stay readable from the provider they were written to.",
            SettingScope.Platform, AllowedValues: ["LocalDisk", "S3", "AzureBlob"]),
        new(SettingCodes.Files.DownloadLinkMinutes, "Download link lifetime (minutes)", SettingDataType.Integer, "5", "Files",
            "How long a pre-signed download link stays valid.", Min: 1, Max: 60),

        new(SettingCodes.Notifications.ErrorAlertRecipients, "Error alert recipients", SettingDataType.StringList, null, "Notifications",
            "Comma-separated addresses that receive unhandled-error alerts. Empty disables alerts.", SettingScope.Platform, IsRequired: false),
        new(SettingCodes.Notifications.ErrorAlertThrottleMinutes, "Error alert throttle (minutes)", SettingDataType.Integer, "15", "Notifications",
            "The same error (by fingerprint) is emailed at most once per this many minutes.", SettingScope.Platform, Min: 1, Max: 1440),
    ];

    private static readonly FrozenDictionary<string, SettingDefinition> ByCode =
        All.ToFrozenDictionary(s => s.Code, StringComparer.Ordinal);

    public static SettingDefinition? Find(string code) => ByCode.GetValueOrDefault(code);

    public static SettingDefinition Get(string code) =>
        Find(code) ?? throw new KeyNotFoundException($"Setting '{code}' is not in the SettingCatalog.");
}
