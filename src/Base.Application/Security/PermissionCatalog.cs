using System.Collections.Frozen;

namespace Base.Application.Security;

/// <summary>Describes one permission for admin screens, seeding and documentation.</summary>
/// <param name="Key">The <see cref="PermissionCodes"/> constant.</param>
/// <param name="GrantedByDefaultTo">Roles that receive it when roles are first seeded.</param>
/// <param name="Implies">Permissions this one includes (e.g. Edit implies View).</param>
/// <param name="IsPlatform">
/// Acts across tenants (or on data all tenants share, like roles), so it only takes effect for users of the platform
/// (default) tenant. A tenant's own administrators never receive it, whatever their roles say.
/// </param>
public sealed record PermissionDefinition(
    string Key,
    string Label,
    string Module,
    string Description,
    IReadOnlyList<string> GrantedByDefaultTo,
    IReadOnlyList<string> Implies,
    bool IsPlatform = false);

/// <summary>
/// The single source of truth for permissions. Declare a permission only when code enforces it.
/// Administrators receive every permission by default.
/// </summary>
public static class PermissionCatalog
{
    /// <summary>Claim type used to carry permissions on the principal (and in role claims).</summary>
    public const string ClaimType = "permission";

    /// <summary>Authorization policy prefix: <c>[Authorize(Policy = "perm:Settings.Edit")]</c>.</summary>
    public const string PolicyPrefix = "perm:";

    private static readonly string[] AdminOnly = [RoleNames.Administrator];
    private static readonly string[] Everyone = [RoleNames.Administrator, RoleNames.User];

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(PermissionCodes.Settings.View, "View settings", "Settings", "See application settings and their values.", AdminOnly, []),
        new(PermissionCodes.Settings.Edit, "Edit settings", "Settings", "Change application settings.", AdminOnly, [PermissionCodes.Settings.View]),
        new(PermissionCodes.Users.View, "View users", "Users", "See user accounts.", AdminOnly, []),
        new(PermissionCodes.Users.Manage, "Manage users", "Users", "Create, lock and edit user accounts.", AdminOnly, [PermissionCodes.Users.View]),
        new(PermissionCodes.Roles.Manage, "Manage roles", "Users", "Create roles and grant permissions. Roles are shared by every tenant.", AdminOnly,
            [PermissionCodes.Users.View], IsPlatform: true),
        new(PermissionCodes.Files.Upload, "Upload files", "Files", "Upload files; users can always read their own uploads.", Everyone, []),
        new(PermissionCodes.Files.ReadAll, "Read all files", "Files", "Download any file in the tenant, not just your own.", AdminOnly, []),
        new(PermissionCodes.Files.DeleteAll, "Delete any file", "Files", "Delete any file in the tenant.", AdminOnly, [PermissionCodes.Files.ReadAll]),
        new(PermissionCodes.Documents.View, "View all documents", "Documents", "See and download every document in the tenant, not only your own.", AdminOnly, []),
        new(PermissionCodes.Documents.Create, "Create documents", "Documents", "Upload and generate documents, and work on your own drafts.", Everyone, []),
        new(PermissionCodes.Documents.Manage, "Manage documents", "Documents", "Edit, finalise, reopen, archive, restore and delete any document.", AdminOnly,
            [PermissionCodes.Documents.View, PermissionCodes.Documents.Create]),
        new(PermissionCodes.Notifications.View, "View notification log", "Notifications", "See every email, SMS, WhatsApp and in-app message sent, skipped or failed.", AdminOnly, []),
        new(PermissionCodes.Notifications.Manage, "Manage notifications", "Notifications", "Resend messages and edit this tenant's notification wording.", AdminOnly, [PermissionCodes.Notifications.View]),
        new(PermissionCodes.Audit.View, "View audit trail", "System", "See who changed what, when, with before and after values.", AdminOnly, []),
        new(PermissionCodes.Api.ClientsManage, "Manage API clients", "Settings", "Create API keys for other systems, with permissions you hold, and rotate or revoke them.", AdminOnly, []),
        new(PermissionCodes.MasterData.Manage, "Manage lookup lists", "Settings", "Add, rename, reorder and hide entries in lists such as titles and departments.", AdminOnly, []),
        new(PermissionCodes.People.View, "View people", "People", "See the people directory and each person's profile.", Everyone, []),
        new(PermissionCodes.People.Manage, "Manage people", "People", "Add people, edit their profiles, change their status and remove them.", AdminOnly, [PermissionCodes.People.View]),
        new(PermissionCodes.Subscription.View, "View subscription", "Subscription", "See this organisation's plan, usage and billing status.", AdminOnly, []),
        new(PermissionCodes.Platform.TenantsView, "View tenants", "Platform", "See every tenant, its subscription and usage.", AdminOnly, [], IsPlatform: true),
        new(PermissionCodes.Platform.TenantsManage, "Manage tenants", "Platform", "Create tenants, manage their domains and subscriptions, suspend and reinstate them.",
            AdminOnly, [PermissionCodes.Platform.TenantsView], IsPlatform: true),
        new(PermissionCodes.Platform.PlansManage, "Manage plans", "Platform", "Create plans and set their prices, trials, features and limits.", AdminOnly,
            [PermissionCodes.Platform.TenantsView], IsPlatform: true),
        new(PermissionCodes.System.HealthView, "View system health", "System", "See health checks, errors and storage usage.", AdminOnly, []),
        new(PermissionCodes.System.JobsManage, "Manage background jobs", "System", "Open the job dashboard and retry or delete jobs.", AdminOnly, [PermissionCodes.System.HealthView]),
    ];

    private static readonly FrozenDictionary<string, PermissionDefinition> ByKey =
        All.ToFrozenDictionary(p => p.Key, StringComparer.Ordinal);

    public static bool Exists(string key) => ByKey.ContainsKey(key);

    public static PermissionDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    public static bool IsPlatform(string key) => ByKey.TryGetValue(key, out var definition) && definition.IsPlatform;

    /// <summary>The permissions that take effect in a tenant: platform ones only in the platform tenant.</summary>
    public static IEnumerable<string> EffectiveIn(IEnumerable<string> permissions, bool platformTenant) =>
        platformTenant ? permissions : permissions.Where(p => !IsPlatform(p));

    /// <summary>Permissions granted to a role on first seed.</summary>
    public static IEnumerable<string> DefaultsFor(string role) =>
        All.Where(p => p.GrantedByDefaultTo.Contains(role, StringComparer.Ordinal)).Select(p => p.Key);

    /// <summary>Adds everything the granted permissions imply, transitively. Unknown keys are dropped.</summary>
    public static IReadOnlySet<string> Expand(IEnumerable<string> granted)
    {
        ArgumentNullException.ThrowIfNull(granted);
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(granted);

        while (pending.TryPop(out var key))
        {
            if (!ByKey.TryGetValue(key, out var definition) || !result.Add(key))
            {
                continue;
            }

            foreach (var implied in definition.Implies)
            {
                pending.Push(implied);
            }
        }

        return result;
    }
}
