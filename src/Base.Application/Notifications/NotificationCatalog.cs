using System.Collections.Frozen;
using Base.Domain.Notifications;

namespace Base.Application.Notifications;

/// <summary>Template codes. A template = a constant here + a <see cref="NotificationCatalog"/> entry + a file in Templates/.</summary>
public static class NotificationTemplates
{
    public static class Identity
    {
        public const string UserInvited = "Identity.UserInvited";
        public const string PasswordReset = "Identity.PasswordReset";
        public const string PasswordChanged = "Identity.PasswordChanged";
    }

    public static class General
    {
        /// <summary>A generic reminder; also the worked example of WhatsApp with SMS fallback.</summary>
        public const string Reminder = "General.Reminder";
    }
}

/// <summary>What users opt in or out of. Categories whose templates are all mandatory can't be switched off.</summary>
public static class NotificationCategories
{
    public const string Security = "Security";
    public const string Reminders = "Reminders";
}

/// <summary>A WhatsApp template approved by Meta: its name, language, and the variables that fill {{1}}, {{2}}, ...</summary>
public sealed record WhatsAppTemplate(string Name, string Language, IReadOnlyList<string> Parameters);

/// <summary>Everything about a notification except its wording, which lives in Templates/{Code}.txt.</summary>
/// <param name="Channels">Channels sent by default, each independently (e.g. Email and InApp).</param>
/// <param name="Variables">Data the caller must supply. Built-ins (<see cref="NotificationCatalog.BuiltInVariables"/>) are added automatically.</param>
/// <param name="IsMandatory">Ignores user preferences (security messages).</param>
/// <param name="IsSensitive">Content contains a secret (sign-in link): sent immediately and never stored.</param>
/// <param name="Fallback">Tried in order when a channel is unavailable or fails (e.g. SMS after WhatsApp).</param>
/// <param name="WhatsApp">The approved template for the WhatsApp channel; required to send on WhatsApp.</param>
public sealed record NotificationTemplateDefinition(
    string Code,
    string Name,
    string Category,
    string Description,
    IReadOnlyList<NotificationChannel> Channels,
    IReadOnlyList<string> Variables,
    bool IsMandatory = false,
    bool IsSensitive = false,
    IReadOnlyList<NotificationChannel>? Fallback = null,
    WhatsAppTemplate? WhatsApp = null);

public static class NotificationCatalog
{
    public const string AppNameVariable = "AppName";
    public const string RecipientNameVariable = "RecipientName";
    public const string PublicBaseUrlVariable = "PublicBaseUrl";

    public static IReadOnlyList<string> BuiltInVariables { get; } = [AppNameVariable, RecipientNameVariable, PublicBaseUrlVariable];

    public static IReadOnlyList<NotificationTemplateDefinition> All { get; } =
    [
        new(NotificationTemplates.Identity.UserInvited, "Invitation", NotificationCategories.Security,
            "Sent when an administrator invites someone. Contains a single-use link to set a password.",
            [NotificationChannel.Email], ["Link"], IsMandatory: true, IsSensitive: true),
        new(NotificationTemplates.Identity.PasswordReset, "Password reset", NotificationCategories.Security,
            "Sent when a password reset is requested. Contains a single-use link.",
            [NotificationChannel.Email], ["Link"], IsMandatory: true, IsSensitive: true),
        new(NotificationTemplates.Identity.PasswordChanged, "Password changed", NotificationCategories.Security,
            "Tells the account owner their password changed, so an unexpected change gets noticed.",
            [NotificationChannel.Email, NotificationChannel.InApp], [], IsMandatory: true),
        new(NotificationTemplates.General.Reminder, "Reminder", NotificationCategories.Reminders,
            "A generic reminder. WhatsApp first, then SMS, then email; always also in-app.",
            [NotificationChannel.WhatsApp, NotificationChannel.InApp], ["Title", "Message"],
            Fallback: [NotificationChannel.Sms, NotificationChannel.Email],
            WhatsApp: new WhatsAppTemplate("general_reminder", "en", ["RecipientName", "Title", "Message"])),
    ];

    private static readonly FrozenDictionary<string, NotificationTemplateDefinition> ByCode =
        All.ToFrozenDictionary(t => t.Code, StringComparer.Ordinal);

    public static NotificationTemplateDefinition? Find(string code) => ByCode.GetValueOrDefault(code);

    public static NotificationTemplateDefinition Get(string code) =>
        Find(code) ?? throw new KeyNotFoundException($"Notification template '{code}' is not in the NotificationCatalog.");

    public static IEnumerable<string> Categories => All.Select(t => t.Category).Distinct(StringComparer.Ordinal);

    /// <summary>A category can be switched off only if at least one of its templates is optional.</summary>
    public static bool IsMandatoryCategory(string category) =>
        All.Where(t => string.Equals(t.Category, category, StringComparison.Ordinal)).All(t => t.IsMandatory);
}
