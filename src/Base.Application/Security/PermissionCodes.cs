namespace Base.Application.Security;

/// <summary>
/// Every permission the code enforces, as constants (<c>Module.Thing.Action</c>). Never gate on string literals:
/// a misspelt key silently denies everyone. Each constant must have a <see cref="PermissionCatalog"/> entry (tested).
/// </summary>
public static class PermissionCodes
{
    public static class Settings
    {
        public const string View = "Settings.View";
        public const string Edit = "Settings.Edit";
    }

    public static class Releases
    {
        public const string Manage = "Releases.Manage";
    }

    public static class Users
    {
        public const string View = "Users.View";
        public const string Manage = "Users.Manage";
    }

    public static class Roles
    {
        public const string Manage = "Roles.Manage";
    }

    public static class Files
    {
        public const string Upload = "Files.Upload";
        public const string ReadAll = "Files.ReadAll";
        public const string DeleteAll = "Files.DeleteAll";
    }

    public static class Documents
    {
        public const string View = "Documents.View";
        public const string Create = "Documents.Create";
        public const string Manage = "Documents.Manage";
    }

    public static class Notifications
    {
        public const string View = "Notifications.View";
        public const string Manage = "Notifications.Manage";
    }

    public static class Messages
    {
        public const string Send = "Messages.Send";
        public const string SendBulk = "Messages.SendBulk";
    }

    public static class Audit
    {
        public const string View = "Audit.View";
    }

    public static class Api
    {
        public const string ClientsManage = "Api.Clients.Manage";
    }

    public static class MasterData
    {
        public const string Manage = "MasterData.Manage";
    }

    public static class People
    {
        public const string View = "People.View";
        public const string Manage = "People.Manage";
    }

    public static class Subscription
    {
        public const string View = "Subscription.View";
    }

    /// <summary>Running the platform itself. Effective only for users of the platform (default) tenant.</summary>
    public static class Platform
    {
        public const string TenantsView = "Platform.Tenants.View";
        public const string TenantsManage = "Platform.Tenants.Manage";
        public const string PlansManage = "Platform.Plans.Manage";
        public const string FlagsManage = "Platform.Flags.Manage";
    }

    public static class System
    {
        public const string HealthView = "System.Health.View";
        public const string JobsManage = "System.Jobs.Manage";
    }
}
