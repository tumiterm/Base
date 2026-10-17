using System.Net.Mail;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.DataExchange;
using Base.Application.Security;

namespace Base.Application.Users;

/// <summary>The user list. Filter: <c>search</c>.</summary>
internal sealed class UsersExporter(IUserAdministration users) : Exporter<UserSummary>
{
    public override string Code => "users";

    public override string Name => "Users";

    public override string Permission => PermissionCodes.Users.View;

    protected override IReadOnlyList<ExportColumn<UserSummary>> Definition { get; } =
    [
        new("Email", u => u.Email),
        new("Name", u => u.DisplayName),
        new("Roles", u => u.Roles),
        new("Locked", u => u.IsLockedOut, ColumnType.Boolean),
        new("Has password", u => u.HasPassword, ColumnType.Boolean),
        new("Last sign-in", u => u.LastSignInAt, ColumnType.DateTime),
        new("Created", u => u.DateCreated, ColumnType.DateTime),
    ];

    protected override Task<PagedResult<UserSummary>> PageAsync(ExportFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        users.SearchAsync(new UserQuery(filter.Text("search"), page, pageSize), cancellationToken);
}

/// <summary>
/// Bulk invite: one row per person. Goes through <see cref="IUserAdministration.InviteAsync"/>, so the same rules apply
/// as inviting one by one (including that nobody can grant a role they don't hold).
/// </summary>
internal sealed class UserInviteImporter(IUserAdministration users, IRoleAdministration roles) : Importer
{
    private const string EmailColumn = "Email";
    private const string NameColumn = "Name";
    private const string RolesColumn = "Roles";

    public override string Code => "users";

    public override string Name => "Invite users";

    public override string Description => "Invites one person per row. Each gets an email to set their own password.";

    public override string Permission => PermissionCodes.Users.Manage;

    public override IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new(EmailColumn, Required: true, Aliases: ["E-mail", "Email address"], MaxLength: 256, Example: "jane@example.com"),
        new(NameColumn, Required: true, Aliases: ["Display name", "Full name"], MaxLength: 100, Example: "Jane Smith"),
        new(RolesColumn, Aliases: ["Role"], Example: "User"),
    ];

    public override async Task<IReadOnlyList<ImportRowError>> ValidateAsync(ImportRow row, ImportContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<ImportRowError>();
        var email = row.Text(EmailColumn)!;
        if (!MailAddress.TryCreate(email, out var address) || !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new ImportRowError(row.Number, EmailColumn, $"'{email}' is not an email address."));
        }
        else if (!context.Seen(EmailColumn).Add(email))
        {
            errors.Add(new ImportRowError(row.Number, EmailColumn, "This email appears earlier in the file."));
        }
        else if ((await users.SearchAsync(new UserQuery(email, 1, 5), cancellationToken)).Items.Any(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new ImportRowError(row.Number, EmailColumn, "A user with this email already exists."));
        }

        var known = await RoleNamesAsync(context, cancellationToken);
        errors.AddRange(row.List(RolesColumn).Where(r => !known.Contains(r)).Select(r => new ImportRowError(row.Number, RolesColumn, $"There is no role called '{r}'.")));
        return errors;
    }

    public override async Task<ValidationResponse> ApplyAsync(ImportRow row, ImportContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(context);
        var known = await RoleNamesAsync(context, cancellationToken);
        var roleNames = row.List(RolesColumn).Select(r => known.First(k => string.Equals(k, r, StringComparison.OrdinalIgnoreCase))).ToList();
        return await users.InviteAsync(new InviteUserRequest(row.Text(EmailColumn)!, row.Text(NameColumn)!, roleNames), cancellationToken);
    }

    private async Task<IReadOnlySet<string>> RoleNamesAsync(ImportContext context, CancellationToken cancellationToken)
    {
        if (context.Items.TryGetValue(nameof(RoleNamesAsync), out var cached) && cached is IReadOnlySet<string> names)
        {
            return names;
        }

        var loaded = (await roles.ListAsync(cancellationToken)).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        context.Items[nameof(RoleNamesAsync)] = loaded;
        return loaded;
    }
}
