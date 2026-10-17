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

        /// <summary>A message across the top of every page (planned maintenance, ...); empty shows nothing.</summary>
        public const string Announcement = "General.Announcement";
    }

    public static class Security
    {
        /// <summary>Switch: whether "Forgot your password?" is offered on the sign-in page.</summary>
        public const string PasswordResetEnabled = "Security.PasswordResetEnabled";
    }

    public static class Releases
    {
        /// <summary>The support window: how many releases an organisation may stay behind (0: no limit).</summary>
        public const string MaxReleasesBehind = "Releases.MaxReleasesBehind";
    }

    public static class Documents
    {
        /// <summary>Switch: whether documents open in the browser (PDF, images, text) or only download.</summary>
        public const string PreviewEnabled = "Documents.PreviewEnabled";
    }

    /// <summary>
    /// Email delivery set up on the settings screen instead of in configuration. When <see cref="SmtpEnabled"/> is on
    /// (and a tenant hasn't picked another provider), email goes through this SMTP server.
    /// </summary>
    public static class Email
    {
        public const string SmtpEnabled = "Email.Smtp.Enabled";
        public const string SmtpHost = "Email.Smtp.Host";
        public const string SmtpPort = "Email.Smtp.Port";
        public const string SmtpUseStartTls = "Email.Smtp.UseStartTls";
        public const string SmtpUserName = "Email.Smtp.UserName";

        /// <summary>A <see cref="SettingDataType.Secret"/>: stored encrypted, never shown again.</summary>
        public const string SmtpPassword = "Email.Smtp.Password";
        public const string FromAddress = "Email.FromAddress";
        public const string FromName = "Email.FromName";
    }

    public static class Branding
    {
        /// <summary>The tenant's accent colour: buttons, links, the active navigation item, focus rings.</summary>
        public const string PrimaryColor = "Branding.PrimaryColor";

        /// <summary>system, light or dark: the theme a user gets until they pick one.</summary>
        public const string DefaultTheme = "Branding.DefaultTheme";

        /// <summary>The stored file id of the tenant's logo; set through <c>IBrandingService</c>, not by hand.</summary>
        public const string LogoFileId = "Branding.LogoFileId";
    }

    public static class Api
    {
        /// <summary>Requests a minute an API client may make when it has no limit of its own.</summary>
        public const string DefaultRequestsPerMinute = "Api.DefaultRequestsPerMinute";

        /// <summary>The longest an API key may be valid; keys can't be made to last forever.</summary>
        public const string KeyLifetimeDays = "Api.KeyLifetimeDays";

        /// <summary>How long a response is kept for replay under its Idempotency-Key.</summary>
        public const string IdempotencyHours = "Api.IdempotencyHours";
    }

    public static class Subscriptions
    {
        /// <summary>Days a past-due subscription keeps access before it is suspended.</summary>
        public const string GraceDays = "Subscriptions.GraceDays";

        /// <summary>How many days before a trial ends its contact is told.</summary>
        public const string TrialReminderDays = "Subscriptions.TrialReminderDays";
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

    public static class System
    {
        public const string ErrorRetentionDays = "System.ErrorRetentionDays";
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
