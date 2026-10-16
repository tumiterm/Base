namespace Base.Application.Users;

public sealed record UserSummary(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool IsLockedOut,
    bool HasPassword,
    DateTimeOffset? LastSignInAt,
    DateTimeOffset DateCreated);

public sealed record UserQuery(string? Search = null, int Page = 1, int PageSize = 25);

/// <summary>Invites a user. No password is set by an administrator: the user chooses one from the emailed link.</summary>
public sealed record InviteUserRequest(string Email, string DisplayName, IReadOnlyList<string> Roles);

public sealed record UpdateUserRequest(string DisplayName, IReadOnlyList<string> Roles);

public sealed record RoleSummary(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<string> Permissions,
    int UserCount,
    bool IsSystem);

public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions);

public enum PasswordTokenKind
{
    Reset = 0,
    Invite = 1,
}

/// <summary>Completes an invite or a password reset from the emailed link.</summary>
public sealed record SetPasswordRequest(Guid UserId, string Token, PasswordTokenKind Kind, string NewPassword, string ConfirmPassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string ConfirmPassword);
