using Base.Application.Common.Persistence;
using Base.Application.Common.Results;

namespace Base.Application.Users;

/// <summary>
/// Administration of the current tenant's users. Every change is subject to the escalation rule: you can only manage
/// users, and grant roles, whose permissions you hold yourself. The last active administrator is protected.
/// </summary>
public interface IUserAdministration
{
    Task<PagedResult<UserSummary>> SearchAsync(UserQuery query, CancellationToken cancellationToken = default);

    Task<ValidationResponse<UserSummary>> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<ValidationResponse<Guid>> InviteAsync(InviteUserRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> UpdateAsync(Guid userId, UpdateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Locks the account indefinitely and ends its sessions within the principal refresh interval.</summary>
    Task<ValidationResponse> LockAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<ValidationResponse> UnlockAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Emails the user a reset link (or a fresh invite if they never set a password).</summary>
    Task<ValidationResponse> SendPasswordResetAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Roles and their permissions. Roles are shared by all tenants; system roles cannot be renamed or deleted.</summary>
public interface IRoleAdministration
{
    Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse<RoleSummary>> GetAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<ValidationResponse<Guid>> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> UpdateAsync(Guid roleId, SaveRoleRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> DeleteAsync(Guid roleId, CancellationToken cancellationToken = default);
}

/// <summary>Self-service account operations.</summary>
public interface IAccountService
{
    /// <summary>Always succeeds, whether or not the email exists, so the form can't be used to discover accounts.</summary>
    Task<ValidationResponse> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Sets a password from an invite or reset link. Tokens are single-use and expire.</summary>
    Task<ValidationResponse> SetPasswordAsync(SetPasswordRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default);
}
