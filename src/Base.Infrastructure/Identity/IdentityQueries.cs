using Base.Application.Security;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Identity;

/// <summary>Read helpers shared by the user and role services. User queries are tenant-filtered; roles are global.</summary>
internal sealed class IdentityQueries(AppDbContext db, TimeProvider time)
{
    public async Task<Dictionary<Guid, List<string>>> RolesOfUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var rows = await (
                from userRole in db.UserRoles
                join role in db.Roles on userRole.RoleId equals role.Id
                where userIds.Contains(userRole.UserId)
                select new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Select(r => r.Name!).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>The expanded permissions the given roles grant together.</summary>
    public async Task<IReadOnlySet<string>> PermissionsOfRolesAsync(IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken)
    {
        if (roleNames.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var normalized = roleNames.Select(r => r.ToUpperInvariant()).ToList();
        var granted = await (
                from claim in db.RoleClaims
                join role in db.Roles on claim.RoleId equals role.Id
                where normalized.Contains(role.NormalizedName!) && claim.ClaimType == PermissionCatalog.ClaimType
                select claim.ClaimValue!)
            .ToListAsync(cancellationToken);

        return PermissionCatalog.Expand(granted);
    }

    public async Task<IReadOnlySet<string>> PermissionsOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roles = (await RolesOfUsersAsync([userId], cancellationToken)).GetValueOrDefault(userId) ?? [];
        return await PermissionsOfRolesAsync(roles, cancellationToken);
    }

    /// <summary>Active (not locked) administrators in this tenant, other than <paramref name="exceptUserId"/>.</summary>
    public Task<int> CountOtherActiveAdministratorsAsync(Guid exceptUserId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var adminRole = RoleNames.Administrator.ToUpperInvariant();
        return (
                from user in db.Users
                join userRole in db.UserRoles on user.Id equals userRole.UserId
                join role in db.Roles on userRole.RoleId equals role.Id
                where role.NormalizedName == adminRole && user.Id != exceptUserId && (user.LockoutEnd == null || user.LockoutEnd <= now)
                select user.Id)
            .Distinct()
            .CountAsync(cancellationToken);
    }
}
