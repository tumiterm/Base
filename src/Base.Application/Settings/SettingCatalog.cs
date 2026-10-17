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

        new(SettingCodes.Branding.PrimaryColor, "Accent colour", SettingDataType.Color, "#4f5bd5", "Branding",
            "Used for buttons, links, the active menu item and focus rings. Text on it is picked automatically for contrast."),
        new(SettingCodes.Branding.DefaultTheme, "Default theme", SettingDataType.String, "system", "Branding",
            "What users see until they choose: follow their device, or always light or dark.", AllowedValues: ["system", "light", "dark"]),
        new(SettingCodes.Branding.LogoFileId, "Logo", SettingDataType.String, null, "Branding",
            "Set from the Appearance page. Shown in the sidebar and on the sign-in page.", IsRequired: false),

        new(SettingCodes.Files.MaxUploadSizeMb, "Maximum upload size (MB)", SettingDataType.Integer, "10", "Files",
            "Largest file a user may upload.", Min: 1, Max: 500),
        new(SettingCodes.Files.AllowedExtensions, "Allowed file types", SettingDataType.StringList, ".pdf,.png,.jpg,.jpeg,.docx,.xlsx", "Files",
            "Comma-separated extensions. Content is also checked against the file signature."),
        new(SettingCodes.Api.DefaultRequestsPerMinute, "API requests per minute", SettingDataType.Integer, "600", "API",
            "How many requests a minute an API client may make unless it has its own limit. Signed-in people get the same limit.",
            SettingScope.Platform, Min: 1, Max: 100000),
        new(SettingCodes.Api.KeyLifetimeDays, "Longest API key lifetime (days)", SettingDataType.Integer, "365", "API",
            "Keys expire after at most this many days and must be rotated.", SettingScope.Platform, Min: 1, Max: 1095),
        new(SettingCodes.Api.IdempotencyHours, "Idempotency window (hours)", SettingDataType.Integer, "24", "API",
            "How long a request sent with an Idempotency-Key is remembered, so a retry gets the first answer instead of doing the work twice.",
            SettingScope.Platform, Min: 1, Max: 168),
        new(SettingCodes.Subscriptions.GraceDays, "Grace period (days)", SettingDataType.Integer, "7", "Subscriptions",
            "How long a tenant keeps access after its trial ends or a renewal isn't paid, before it is suspended.",
            SettingScope.Platform, Min: 0, Max: 90),
        new(SettingCodes.Subscriptions.TrialReminderDays, "Trial reminder (days before)", SettingDataType.Integer, "3", "Subscriptions",
            "How many days before a trial ends its contact is emailed.", SettingScope.Platform, Min: 1, Max: 30),
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

        new(SettingCodes.Exports.MaxImmediateRows, "Exports: download at once up to (rows)", SettingDataType.Integer, "5000", "Data exchange",
            "Exports with more rows run in the background; the user is notified in-app when the file is ready.", Min: 1, Max: 100000),
        new(SettingCodes.Exports.MaxRows, "Exports: most rows", SettingDataType.Integer, "100000", "Data exchange",
            "Exports with more rows are refused; narrow the filter instead.", SettingScope.Platform, Min: 1, Max: 1000000),
        new(SettingCodes.Exports.RetentionDays, "Exports: keep files (days)", SettingDataType.Integer, "7", "Data exchange",
            "Background export files can be downloaded for this long, then they are deleted.", Min: 1, Max: 90),
        new(SettingCodes.Imports.MaxRows, "Imports: most rows", SettingDataType.Integer, "10000", "Data exchange",
            "Files with more data rows are refused; split them.", SettingScope.Platform, Min: 1, Max: 100000),
        new(SettingCodes.Imports.RetentionDays, "Imports: keep uploaded files (days)", SettingDataType.Integer, "30", "Data exchange",
            "Uploaded import files (and their error reports) are kept this long.", Min: 1, Max: 365),

        new(SettingCodes.System.ErrorRetentionDays, "Error log retention (days)", SettingDataType.Integer, "90", "System",
            "Recorded unhandled errors (by ticket number) older than this are deleted by a daily job.", SettingScope.Platform, Min: 7, Max: 3650),

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
