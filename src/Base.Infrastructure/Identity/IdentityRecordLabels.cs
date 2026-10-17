using Base.Application.Common.Abstractions;
using Base.Application.Common.Records;
using Base.Application.Security;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Identity;

/// <summary>Names users (audit name "User") for screens that point at them. Hidden without <c>Users.View</c>.</summary>
internal sealed class UserRecordLabels(AppDbContext db, ICurrentUser currentUser) : IRecordLabelProvider
{
    public string EntityType => "User";

    public string TypeName => "User";

    public async Task<IReadOnlyDictionary<string, RecordLabel>> GetLabelsAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default)
    {
        var ids = RecordIds.Guids(entityIds);
        if (ids.Count == 0 || !currentUser.HasPermission(PermissionCodes.Users.View))
        {
            return new Dictionary<string, RecordLabel>(StringComparer.Ordinal);
        }

        // The tenant filter keeps this to the current tenant's users.
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName, u.Email }).ToListAsync(cancellationToken);
        return RecordIds.ByStoredId(entityIds, users.ToDictionary(u => u.Id, u => new RecordLabel(u.DisplayName, u.Email)));
    }
}

/// <summary>Names roles (audit name "Role"). Roles are shared, so anyone who manages users or roles may see them.</summary>
internal sealed class RoleRecordLabels(AppDbContext db, ICurrentUser currentUser) : IRecordLabelProvider
{
    public string EntityType => "Role";

    public string TypeName => "Role";

    public async Task<IReadOnlyDictionary<string, RecordLabel>> GetLabelsAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default)
    {
        var ids = RecordIds.Guids(entityIds);
        if (ids.Count == 0 || !(currentUser.HasPermission(PermissionCodes.Users.View) || currentUser.HasPermission(PermissionCodes.Roles.Manage)))
        {
            return new Dictionary<string, RecordLabel>(StringComparer.Ordinal);
        }

        var roles = await db.Roles.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.Name, r.Description }).ToListAsync(cancellationToken);
        return RecordIds.ByStoredId(entityIds, roles.ToDictionary(r => r.Id, r => new RecordLabel(r.Name ?? "Role", r.Description)));
    }
}
