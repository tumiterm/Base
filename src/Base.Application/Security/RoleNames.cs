namespace Base.Application.Security;

/// <summary>Roles seeded on first run. Roles are data: their permissions are grantable, never hard-coded checks.</summary>
public static class RoleNames
{
    public const string Administrator = "Administrator";
    public const string User = "User";

    public static IReadOnlyList<string> Seeded { get; } = [Administrator, User];

    /// <summary>What a seeded role is for, shown where roles are picked. Administrators can reword it.</summary>
    public static string? DescriptionOf(string role) => role switch
    {
        Administrator => "Everything: users, roles, settings and all the organisation's data.",
        User => "Day-to-day work with the organisation's records. No administration.",
        _ => null,
    };
}
