using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Common.Validation;
using Base.Application.Security;
using Base.Application.Users;
using Base.Domain.Common;
using Base.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Identity;

internal sealed class RoleAdministrationService(
    RoleManager<ApplicationRole> roles,
    AppDbContext db,
    IUnitOfWork unitOfWork,
    ICurrentUser actor,
    IValidator<SaveRoleRequest> validator) : IRoleAdministration
{
    public async Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);
        var permissions = await PermissionsByRoleAsync(null, cancellationToken);

        // Counts only this tenant's users (the Users query filter applies through the join).
        var counts = await (
                from userRole in db.UserRoles
                join user in db.Users on userRole.UserId equals user.Id
                group userRole by userRole.RoleId into g
                select new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);

        return all.Select(r => ToSummary(r, permissions.GetValueOrDefault(r.Id), counts.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<ValidationResponse<RoleSummary>> GetAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var summary = (await ListAsync(cancellationToken)).FirstOrDefault(r => r.Id == roleId);
        return summary is null ? ValidationResponse.NotFound("Role not found.").As<RoleSummary>() : summary;
    }

    public async Task<ValidationResponse<Guid>> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation.As<Guid>();
        }

        var guard = PermissionGuard.EnsureHoldsAll(actor, request.Permissions, "create a role with these permissions");
        if (guard.IsError)
        {
            return guard.As<Guid>();
        }

        if (await roles.RoleExistsAsync(request.Name.Trim()))
        {
            return ValidationResponse.Failure(new ValidationError(nameof(SaveRoleRequest.Name), "A role with this name already exists.", "role.duplicate")).As<Guid>();
        }

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var role = new ApplicationRole(request.Name.Trim()) { Id = SequentialGuid.Next(), Description = Clean(request.Description) };
                IdentityOperationException.ThrowIfFailed(await roles.CreateAsync(role));
                AddPermissions(role.Id, request.Permissions);
                await db.SaveChangesAsync(ct);
                return role.Id;
            }, cancellationToken);
        }
        catch (IdentityOperationException exception)
        {
            return exception.Response.As<Guid>();
        }
    }

    public async Task<ValidationResponse> UpdateAsync(Guid roleId, SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation;
        }

        var role = await roles.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return ValidationResponse.NotFound("Role not found.");
        }

        var name = request.Name.Trim();
        var isSystem = IsSystem(role.Name);
        if (isSystem && !string.Equals(name, role.Name, StringComparison.Ordinal))
        {
            return ValidationResponse.Conflict($"'{role.Name}' is a system role and can't be renamed.");
        }

        var current = (await PermissionsByRoleAsync(roleId, cancellationToken)).GetValueOrDefault(roleId) ?? [];
        var wanted = request.Permissions.Distinct(StringComparer.Ordinal).ToList();
        if (string.Equals(role.Name, RoleNames.Administrator, StringComparison.Ordinal) && wanted.Count != PermissionCatalog.All.Count)
        {
            return ValidationResponse.Conflict("Administrators always hold every permission.");
        }

        var toAdd = wanted.Except(current, StringComparer.Ordinal).ToList();
        var toRemove = current.Except(wanted, StringComparer.Ordinal).ToList();
        var guard = PermissionGuard.EnsureHoldsAll(actor, toAdd.Concat(toRemove), "change these permissions");
        if (guard.IsError)
        {
            return guard;
        }

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                role.Name = name;
                role.Description = Clean(request.Description);
                IdentityOperationException.ThrowIfFailed(await roles.UpdateAsync(role));

                AddPermissions(role.Id, toAdd);
                await db.RoleClaims
                    .Where(c => c.RoleId == role.Id && c.ClaimType == PermissionCatalog.ClaimType && toRemove.Contains(c.ClaimValue!))
                    .ExecuteDeleteAsync(ct);
                await db.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        catch (IdentityOperationException exception)
        {
            return exception.Response;
        }

        // Signed-in users pick the change up when their principal is refreshed (within a minute).
        return ValidationResponse.Success("Role saved.");
    }

    public async Task<ValidationResponse> DeleteAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await roles.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return ValidationResponse.NotFound("Role not found.");
        }

        if (IsSystem(role.Name))
        {
            return ValidationResponse.Conflict($"'{role.Name}' is a system role and can't be deleted.");
        }

        // UserRoles has no tenant filter: this sees assignments in every tenant, because roles are shared.
        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == roleId, cancellationToken))
        {
            return ValidationResponse.Conflict("The role is still assigned to users. Remove it from them first.");
        }

        var permissions = (await PermissionsByRoleAsync(roleId, cancellationToken)).GetValueOrDefault(roleId) ?? [];
        var guard = PermissionGuard.EnsureHoldsAll(actor, permissions, "delete this role");
        if (guard.IsError)
        {
            return guard;
        }

        var result = await roles.DeleteAsync(role);
        return result.Succeeded ? ValidationResponse.Success("Role deleted.") : result.ToValidationResponse();
    }

    private static bool IsSystem(string? roleName) => RoleNames.Seeded.Contains(roleName, StringComparer.Ordinal);

    private static string? Clean(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private void AddPermissions(Guid roleId, IEnumerable<string> permissions)
    {
        foreach (var permission in permissions)
        {
            db.RoleClaims.Add(new IdentityRoleClaim<Guid> { RoleId = roleId, ClaimType = PermissionCatalog.ClaimType, ClaimValue = permission });
        }
    }

    private async Task<Dictionary<Guid, List<string>>> PermissionsByRoleAsync(Guid? roleId, CancellationToken cancellationToken)
    {
        var claims = db.RoleClaims.AsNoTracking().Where(c => c.ClaimType == PermissionCatalog.ClaimType);
        if (roleId is { } id)
        {
            claims = claims.Where(c => c.RoleId == id);
        }

        var rows = await claims.Select(c => new { c.RoleId, c.ClaimValue }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.RoleId).ToDictionary(g => g.Key, g => g.Select(r => r.ClaimValue!).Order(StringComparer.Ordinal).ToList());
    }

    private static RoleSummary ToSummary(ApplicationRole role, List<string>? permissions, int userCount) =>
        new(role.Id, role.Name ?? string.Empty, role.Description, permissions ?? [], userCount, IsSystem(role.Name));
}
