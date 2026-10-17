using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Common.Validation;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Application.Users;
using Base.Domain.Common;
using Base.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Identity;

internal sealed class UserAdministrationService(
    UserManager<ApplicationUser> users,
    RoleManager<ApplicationRole> roles,
    AppDbContext db,
    IUnitOfWork unitOfWork,
    IdentityQueries queries,
    ICurrentUser actor,
    IPlatformTenant platform,
    ITenantEntitlements entitlements,
    TimeProvider time,
    IValidator<InviteUserRequest> inviteValidator,
    IValidator<UpdateUserRequest> updateValidator) : IUserAdministration
{
    public async Task<PagedResult<UserSummary>> SearchAsync(UserQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);

        var matches = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            matches = matches.Where(u => u.DisplayName.Contains(term) || u.Email!.Contains(term));
        }

        var total = await matches.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResult<UserSummary>.Empty(page);
        }

        var rows = await matches
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Email)
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(cancellationToken);

        var userRoles = await queries.RolesOfUsersAsync(rows.Select(u => u.Id).ToList(), cancellationToken);
        return new PagedResult<UserSummary>(rows.Select(u => ToSummary(u, userRoles.GetValueOrDefault(u.Id))).ToList(), total, page.Page, page.PageSize);
    }

    public async Task<ValidationResponse<UserSummary>> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ValidationResponse.NotFound("User not found.").As<UserSummary>();
        }

        var userRoles = await queries.RolesOfUsersAsync([userId], cancellationToken);
        return ToSummary(user, userRoles.GetValueOrDefault(userId));
    }

    public async Task<ValidationResponse<Guid>> InviteAsync(InviteUserRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await inviteValidator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation.As<Guid>();
        }

        var email = request.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return ValidationResponse.Failure(new ValidationError(nameof(InviteUserRequest.Email), "A user with this email already exists.", "user.duplicate")).As<Guid>();
        }

        var roleCheck = await CheckAssignableRolesAsync(request.Roles, cancellationToken);
        if (roleCheck.IsError)
        {
            return roleCheck.As<Guid>();
        }

        var limit = await entitlements.EnsureWithinLimitAsync(LimitCodes.Users, 1, cancellationToken);
        if (limit.IsError)
        {
            return limit.As<Guid>();
        }

        return await RunAsync(async ct =>
        {
            // Built inside the operation so a retried transaction starts from a fresh entity and event.
            var user = new ApplicationUser
            {
                Id = SequentialGuid.Next(),
                UserName = email,
                Email = email,
                DisplayName = request.DisplayName.Trim(),
            };
            user.Raise(new UserInvited(user.Id, request.Welcome));

            IdentityOperationException.ThrowIfFailed(await users.CreateAsync(user));
            if (request.Roles.Count > 0)
            {
                IdentityOperationException.ThrowIfFailed(await users.AddToRolesAsync(user, request.Roles));
            }

            return user.Id;
        }, cancellationToken);
    }

    public async Task<ValidationResponse> UpdateAsync(Guid userId, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await updateValidator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation;
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ValidationResponse.NotFound("User not found.");
        }

        var manageable = await CheckCanManageAsync(user, "change this user", cancellationToken);
        if (manageable.IsError)
        {
            return manageable;
        }

        var roleCheck = await CheckAssignableRolesAsync(request.Roles, cancellationToken);
        if (roleCheck.IsError)
        {
            return roleCheck;
        }

        var current = await users.GetRolesAsync(user);
        var toRemove = current.Except(request.Roles, StringComparer.OrdinalIgnoreCase).ToList();
        var toAdd = request.Roles.Except(current, StringComparer.OrdinalIgnoreCase).ToList();

        if (toRemove.Contains(RoleNames.Administrator, StringComparer.OrdinalIgnoreCase)
            && !user.IsLockedOut(time.GetUtcNow())
            && await queries.CountOtherActiveAdministratorsAsync(user.Id, cancellationToken) == 0)
        {
            return ValidationResponse.Conflict("This is the last active administrator. Make someone else an administrator first.");
        }

        var result = await RunAsync(async _ =>
        {
            user.DisplayName = request.DisplayName.Trim();
            IdentityOperationException.ThrowIfFailed(await users.UpdateAsync(user));
            if (toRemove.Count > 0)
            {
                IdentityOperationException.ThrowIfFailed(await users.RemoveFromRolesAsync(user, toRemove));
            }

            if (toAdd.Count > 0)
            {
                IdentityOperationException.ThrowIfFailed(await users.AddToRolesAsync(user, toAdd));
            }

            return true;
        }, cancellationToken);

        return result.IsSuccess ? ValidationResponse.Success("User saved.") : result;
    }

    public async Task<ValidationResponse> LockAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == actor.UserId)
        {
            return ValidationResponse.Conflict("You can't lock your own account.");
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ValidationResponse.NotFound("User not found.");
        }

        var manageable = await CheckCanManageAsync(user, "lock this user", cancellationToken);
        if (manageable.IsError)
        {
            return manageable;
        }

        if (await users.IsInRoleAsync(user, RoleNames.Administrator) && await queries.CountOtherActiveAdministratorsAsync(user.Id, cancellationToken) == 0)
        {
            return ValidationResponse.Conflict("This is the last active administrator and can't be locked.");
        }

        var result = await RunAsync(async _ =>
        {
            IdentityOperationException.ThrowIfFailed(await users.SetLockoutEnabledAsync(user, enabled: true));
            IdentityOperationException.ThrowIfFailed(await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue));

            // A new stamp makes the security-stamp validator sign the user out at its next check.
            IdentityOperationException.ThrowIfFailed(await users.UpdateSecurityStampAsync(user));
            return true;
        }, cancellationToken);

        return result.IsSuccess ? ValidationResponse.Success("User locked.") : result;
    }

    public async Task<ValidationResponse> UnlockAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ValidationResponse.NotFound("User not found.");
        }

        var manageable = await CheckCanManageAsync(user, "unlock this user", cancellationToken);
        if (manageable.IsError)
        {
            return manageable;
        }

        var result = await RunAsync(async _ =>
        {
            IdentityOperationException.ThrowIfFailed(await users.SetLockoutEndDateAsync(user, null));
            IdentityOperationException.ThrowIfFailed(await users.ResetAccessFailedCountAsync(user));
            return true;
        }, cancellationToken);

        return result.IsSuccess ? ValidationResponse.Success("User unlocked.") : result;
    }

    public async Task<ValidationResponse> SendPasswordResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ValidationResponse.NotFound("User not found.");
        }

        var manageable = await CheckCanManageAsync(user, "reset this user's password", cancellationToken);
        if (manageable.IsError)
        {
            return manageable;
        }

        user.Raise(user.HasPassword ? new PasswordResetRequested(user.Id) : new UserInvited(user.Id));
        var result = await users.UpdateAsync(user);
        return result.Succeeded
            ? ValidationResponse.Success(user.HasPassword ? "Password reset email sent." : "Invitation sent again.")
            : result.ToValidationResponse();
    }

    /// <summary>You can only manage users whose access you hold yourself.</summary>
    private async Task<ValidationResponse> CheckCanManageAsync(ApplicationUser user, string action, CancellationToken cancellationToken) =>
        PermissionGuard.EnsureHoldsAll(actor, await EffectiveAsync(await queries.PermissionsOfUserAsync(user.Id, cancellationToken), cancellationToken), action);

    /// <summary>
    /// Roles are shared by all tenants, so a role can carry platform permissions that do nothing outside the platform
    /// tenant. They are left out of the escalation check there; otherwise no tenant could manage its own administrators.
    /// </summary>
    private async Task<IEnumerable<string>> EffectiveAsync(IEnumerable<string> permissions, CancellationToken cancellationToken) =>
        PermissionCatalog.EffectiveIn(permissions, await platform.IsCurrentAsync(cancellationToken));

    /// <summary>Roles must exist, and you can only assign roles whose permissions you hold.</summary>
    private async Task<ValidationResponse> CheckAssignableRolesAsync(IReadOnlyList<string> roleNames, CancellationToken cancellationToken)
    {
        foreach (var roleName in roleNames)
        {
            if (!await roles.RoleExistsAsync(roleName))
            {
                return ValidationResponse.Failure(new ValidationError("Roles", $"Role '{roleName}' does not exist.", "role.unknown"));
            }
        }

        return PermissionGuard.EnsureHoldsAll(actor, await EffectiveAsync(await queries.PermissionsOfRolesAsync(roleNames, cancellationToken), cancellationToken), "assign these roles");
    }

    private async Task<ValidationResponse<T>> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(operation, cancellationToken);
        }
        catch (IdentityOperationException exception)
        {
            return exception.Response.As<T>();
        }
    }

    private UserSummary ToSummary(ApplicationUser user, IReadOnlyList<string>? roleNames) => new(
        user.Id,
        user.Email ?? string.Empty,
        user.DisplayName,
        roleNames ?? [],
        user.IsLockedOut(time.GetUtcNow()),
        user.HasPassword,
        user.LastSignInAt,
        user.DateCreated);
}
