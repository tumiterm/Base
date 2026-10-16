using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;

namespace Base.Application.Security;

/// <summary>
/// Stops privilege escalation: nobody can grant, remove, or manage access they don't hold themselves.
/// A user with <c>Roles.Manage</c> but not <c>Settings.Edit</c> cannot create a role with <c>Settings.Edit</c>,
/// assign it, or lock out someone who has it.
/// </summary>
public static class PermissionGuard
{
    public static ValidationResponse EnsureHoldsAll(ICurrentUser actor, IEnumerable<string> permissions, string action)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(permissions);

        var missing = permissions.Where(p => !actor.HasPermission(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0
            ? ValidationResponse.Success()
            : ValidationResponse.Forbidden($"You can't {action} because it involves permissions you don't have: {string.Join(", ", missing)}.");
    }
}
