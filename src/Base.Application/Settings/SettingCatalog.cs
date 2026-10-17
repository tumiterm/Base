using System.Collections.Frozen;
using Base.Application.Files;

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
            "Where new uploads are stored. Existing files stay readable from the provider they were written to. The capped Database provider is chosen per category instead (Files.CategoryProviders).",
            SettingScope.Platform, AllowedValues: [FileStorageProviderNames.LocalDisk, FileStorageProviderNames.S3, FileStorageProviderNames.AzureBlob]),
        new(SettingCodes.Files.DownloadLinkMinutes, "Download link lifetime (minutes)", SettingDataType.Integer, "5", "Files",
            "How long a pre-signed download link stays valid.", Min: 1, Max: 60),
        new(SettingCodes.Files.CategoryProviders, "Storage provider per category", SettingDataType.StringList, null, "Files",
            "Comma-separated category=Provider pairs, e.g. avatar=Database,signature=Database. Other categories use the default provider.",
            SettingScope.Platform, IsRequired: false),
        new(SettingCodes.Files.DeletedRetentionDays, "Keep deleted files (days)", SettingDataType.Integer, "30", "Files",
            "Deleted and expired files can be restored for this long; then a daily job removes their content for good.",
            SettingScope.Platform, Min: 1, Max: 3650),
        new(SettingCodes.Files.DatabaseMaxFileSizeKb, "Database storage: largest file (KB)", SettingDataType.Integer, "1024", "Files",
            "Files stored in the database provider may be at most this big. Larger files belong in blob storage.",
            SettingScope.Platform, Min: 1, Max: 51200),
        new(SettingCodes.Files.DatabaseTenantQuotaMb, "Database storage: quota per tenant (MB)", SettingDataType.Integer, "100", "Files",
            "Total size of the files one tenant may keep in the database provider, including deleted files not yet purged. 0 = no limit.",
            SettingScope.Platform, Min: 0, Max: 102400),

        new(SettingCodes.Audit.RetentionDays, "Audit retention (days)", SettingDataType.Integer, "730", "Audit",
            "Audit entries older than this are deleted by a daily job. Check your legal retention obligations first.",
            SettingScope.Platform, Min: 30, Max: 3650),

        new(SettingCodes.Notifications.EmailProvider, "Email provider", SettingDataType.String, null, "Notifications",
            "Provider instance name from configuration (Notifications:Providers). Empty uses the Email channel's default.", IsRequired: false),
        new(SettingCodes.Notifications.SmsProvider, "SMS provider", SettingDataType.String, null, "Notifications",
            "Provider instance name from configuration. Empty uses the SMS channel's default.", IsRequired: false),
        new(SettingCodes.Notifications.WhatsAppProvider, "WhatsApp provider", SettingDataType.String, null, "Notifications",
            "Provider instance name from configuration. Empty uses the WhatsApp channel's default.", IsRequired: false),
        new(SettingCodes.Notifications.DefaultCountryCode, "Default country calling code", SettingDataType.Integer, "27", "Notifications",
            "Added to local phone numbers that start with 0 (27 = South Africa).", Min: 1, Max: 999),
        new(SettingCodes.Notifications.RetentionDays, "Notification log retention (days)", SettingDataType.Integer, "365", "Notifications",
            "Messages older than this are deleted by a daily job, including in-app notifications.", SettingScope.Platform, Min: 30, Max: 3650),
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
