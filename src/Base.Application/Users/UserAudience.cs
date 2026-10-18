using System.Runtime.CompilerServices;
using Base.Application.Common.Persistence;
using Base.Application.Communications;
using Base.Application.Security;

namespace Base.Application.Users;

/// <summary>The people who sign in, by role: the audience for in-app messages, and email to staff.</summary>
internal sealed class UserAudience(IUserAdministration users, IRoleAdministration roles) : IAudienceSource
{
    private const string RoleFilter = "role";
    private const string StatusFilter = "status";
    private const string Active = "active";
    private const string Invited = "invited";

    public string Code => AudienceCodes.Users;

    public string Name => "Users";

    public string Description => "People who sign in to this organisation, by role. The only audience that receives in-app messages.";

    public string Icon => "users";

    public string Permission => PermissionCodes.Users.View;

    public IReadOnlyList<AudienceField> Fields { get; } = [new("Roles", "Roles")];

    public async Task<IReadOnlyList<AudienceFilter>> FiltersAsync(CancellationToken cancellationToken = default) =>
    [
        new(RoleFilter, "Role", (await roles.ListAsync(cancellationToken)).Select(r => new AudienceOption(r.Name, r.Name)).ToList()),
        new(StatusFilter, "Status", [new(Active, "Active"), new(Invited, "Invited, not accepted yet")]),
    ];

    public async IAsyncEnumerable<AudienceMember> MembersAsync(IReadOnlyDictionary<string, string> filters, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var role = filters.GetValueOrDefault(RoleFilter);
        var status = filters.GetValueOrDefault(StatusFilter);
        await foreach (var user in AsyncEnumerables.Pages(PageRequest.MaxPageSize, async page => (await users.SearchAsync(new UserQuery(null, page, PageRequest.MaxPageSize), cancellationToken)).Items, cancellationToken))
        {
            if (user.IsLockedOut
                || (role is not null && !user.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
                || (string.Equals(status, Active, StringComparison.Ordinal) && !user.HasPassword)
                || (string.Equals(status, Invited, StringComparison.Ordinal) && user.HasPassword))
            {
                continue;
            }

            yield return ToMember(user);
        }
    }

    public async Task<IReadOnlyList<AudienceMember>> FindAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var members = new List<AudienceMember>();
        foreach (var key in keys)
        {
            if (Guid.TryParse(key, out var id) && (await users.GetAsync(id, cancellationToken)).Data is { IsLockedOut: false } user)
            {
                members.Add(ToMember(user));
            }
        }

        return members;
    }

    public async Task<IReadOnlyList<AudienceMember>> SearchAsync(string term, int take, CancellationToken cancellationToken = default) =>
        (await users.SearchAsync(new UserQuery(term, 1, Math.Clamp(take, 1, PageRequest.MaxPageSize)), cancellationToken)).Items
            .Where(u => !u.IsLockedOut).Select(ToMember).ToList();

    // The account supplies the phone number at send time; the email is the sign-in address.
    private static AudienceMember ToMember(UserSummary user) =>
        new(user.Id.ToString(), user.DisplayName, user.Email, null, user.Id, new Dictionary<string, string?>(StringComparer.Ordinal) { ["Roles"] = string.Join(", ", user.Roles) });
}
