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

    public static class Audit
    {
        public const string View = "Audit.View";
    }

    public static class System
    {
        public const string HealthView = "System.Health.View";
        public const string JobsManage = "System.Jobs.Manage";
    }
}
