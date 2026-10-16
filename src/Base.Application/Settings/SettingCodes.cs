namespace Base.Application.Settings;

/// <summary>Setting keys. Adding a setting = a constant here + a <see cref="SettingCatalog"/> entry. No migration.</summary>
public static class SettingCodes
{
    public static class General
    {
        public const string ApplicationName = "General.ApplicationName";

        /// <summary>IANA zone used to display UTC timestamps (storage is always UTC).</summary>
        public const string DisplayTimeZone = "General.DisplayTimeZone";
    }

    public static class Files
    {
        public const string MaxUploadSizeMb = "Files.MaxUploadSizeMb";
        public const string AllowedExtensions = "Files.AllowedExtensions";
        public const string DefaultProvider = "Files.DefaultProvider";
        public const string DownloadLinkMinutes = "Files.DownloadLinkMinutes";
    }

    public static class Notifications
    {
        public const string ErrorAlertRecipients = "Notifications.ErrorAlertRecipients";
        public const string ErrorAlertThrottleMinutes = "Notifications.ErrorAlertThrottleMinutes";
    }
}
