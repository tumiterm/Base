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
    }

    public static class Audit
    {
        public const string RetentionDays = "Audit.RetentionDays";
    }

    public static class Notifications
    {
        public const string ErrorAlertRecipients = "Notifications.ErrorAlertRecipients";
        public const string ErrorAlertThrottleMinutes = "Notifications.ErrorAlertThrottleMinutes";
    }
}
