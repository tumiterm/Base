namespace Base.Application.Settings;

/// <summary>Setting keys. Adding a setting = a constant here + a <see cref="SettingCatalog"/> entry. No migration.</summary>
public static class SettingCodes
{
    public static class General
    {
        public const string ApplicationName = "General.ApplicationName";

        /// <summary>IANA zone used to display UTC timestamps (storage is always UTC).</summary>
        public const string DisplayTimeZone = "General.DisplayTimeZone";

        /// <summary>Absolute URL used in emailed links. Never derived from the request's Host header (link hijacking).</summary>
        public const string PublicBaseUrl = "General.PublicBaseUrl";
    }

    public static class Files
    {
        public const string MaxUploadSizeMb = "Files.MaxUploadSizeMb";
        public const string AllowedExtensions = "Files.AllowedExtensions";
        public const string DefaultProvider = "Files.DefaultProvider";
        public const string DownloadLinkMinutes = "Files.DownloadLinkMinutes";

        /// <summary><c>category=Provider</c> pairs that send some categories somewhere other than the default provider.</summary>
        public const string CategoryProviders = "Files.CategoryProviders";

        /// <summary>Days a deleted (or expired) file is kept before its bytes are purged.</summary>
        public const string DeletedRetentionDays = "Files.DeletedRetentionDays";

        public const string DatabaseMaxFileSizeKb = "Files.Database.MaxFileSizeKb";

        /// <summary>Total bytes one tenant may keep in the database provider; 0 means no limit.</summary>
        public const string DatabaseTenantQuotaMb = "Files.Database.TenantQuotaMb";
    }

    public static class Exports
    {
        /// <summary>Exports up to this many rows download at once; bigger ones run in the background.</summary>
        public const string MaxImmediateRows = "Exports.MaxImmediateRows";
        public const string MaxRows = "Exports.MaxRows";
        public const string RetentionDays = "Exports.RetentionDays";
    }

    public static class Imports
    {
        public const string MaxRows = "Imports.MaxRows";
        public const string RetentionDays = "Imports.RetentionDays";
    }

    public static class Audit
    {
        public const string RetentionDays = "Audit.RetentionDays";
    }

    public static class Notifications
    {
        /// <summary>Provider instance (from configuration) for each channel in this tenant; empty uses the channel's default.</summary>
        public const string EmailProvider = "Notifications.EmailProvider";
        public const string SmsProvider = "Notifications.SmsProvider";
        public const string WhatsAppProvider = "Notifications.WhatsAppProvider";

        /// <summary>Country calling code added to local phone numbers that start with 0.</summary>
        public const string DefaultCountryCode = "Notifications.DefaultCountryCode";
        public const string RetentionDays = "Notifications.RetentionDays";

        public const string ErrorAlertRecipients = "Notifications.ErrorAlertRecipients";
        public const string ErrorAlertThrottleMinutes = "Notifications.ErrorAlertThrottleMinutes";
    }
}
