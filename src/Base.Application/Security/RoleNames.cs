namespace Base.Application.Security;

/// <summary>Roles seeded on first run. Roles are data: their permissions are grantable, never hard-coded checks.</summary>
public static class RoleNames
{
    public const string Administrator = "Administrator";
    public const string User = "User";

    public static IReadOnlyList<string> Seeded { get; } = [Administrator, User];
}
