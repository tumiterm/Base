using System.Net.Mail;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.DataExchange;
using Base.Application.MasterData;
using Base.Application.Security;
using Base.Domain.People;

namespace Base.Application.People;

/// <summary>The people list as it is filtered on screen. Filters: <c>search</c>, <c>status</c>, <c>department</c>, <c>skill</c>.</summary>
internal sealed class PeopleExporter(IPersonService people, ILookupService lookups) : Exporter<PersonExportRow>
{
    public override string Code => "people";

    public override string Name => "People";

    public override string Permission => PermissionCodes.People.View;

    protected override IReadOnlyList<ExportColumn<PersonExportRow>> Definition { get; } =
    [
        new("First name", r => r.Person.FirstName),
        new("Last name", r => r.Person.LastName),
        new("Email", r => r.Person.Email),
        new("Job title", r => r.Person.JobTitle),
        new("Department", r => r.Department),
        new("Status", r => r.Person.Status.ToString()),
        new("Start date", r => r.Person.StartDate, ColumnType.Date),
        new("Skills", r => r.Skills),
        new("Updated", r => r.Person.DateModified, ColumnType.DateTime),
    ];

    protected override async Task<PagedResult<PersonExportRow>> PageAsync(ExportFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var departments = (await lookups.GetAsync(LookupLists.People.Department, includeInactive: true, cancellationToken)).ToDictionary(v => v.Code, v => v.Name, StringComparer.Ordinal);
        var skills = (await lookups.GetAsync(LookupLists.People.Skill, includeInactive: true, cancellationToken)).ToDictionary(v => v.Code, v => v.Name, StringComparer.Ordinal);
        var result = await people.SearchAsync(
            new PersonQuery(filter.Text("search"), filter.Enum<PersonStatus>("status"), filter.Text("department"), filter.Text("skill"), Page: page, PageSize: pageSize),
            cancellationToken);
        var rows = result.Items.Select(p => new PersonExportRow(
            p,
            p.DepartmentCode is { } d ? departments.GetValueOrDefault(d, d) : null,
            string.Join(", ", p.Skills.Select(s => skills.GetValueOrDefault(s, s))))).ToList();
        return new PagedResult<PersonExportRow>(rows, result.Total, result.Page, result.PageSize);
    }
}

public sealed record PersonExportRow(PersonSummary Person, string? Department, string Skills);

/// <summary>
/// Adds people, or updates the person with the same email. Lookup columns take the entry's name or code. Each row goes
/// through <see cref="IPersonService"/>, so it gets the same validation as the form.
/// </summary>
internal sealed class PeopleImporter(IPersonService people, ILookupService lookups) : Importer
{
    private const string FirstNameColumn = "First name";
    private const string LastNameColumn = "Last name";
    private const string EmailColumn = "Email";
    private const string PhoneColumn = "Phone";
    private const string JobTitleColumn = "Job title";
    private const string DepartmentColumn = "Department";
    private const string StartDateColumn = "Start date";
    private const string SkillsColumn = "Skills";

    public override string Code => "people";

    public override string Name => "People";

    public override string Description => "Adds one person per row, or updates the person who has the row's email.";

    public override string Permission => PermissionCodes.People.Manage;

    public override IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new(FirstNameColumn, Required: true, Aliases: ["First", "Given name"], MaxLength: Person.NameMaxLength, Example: "Thandi"),
        new(LastNameColumn, Required: true, Aliases: ["Last", "Surname", "Family name"], MaxLength: Person.NameMaxLength, Example: "Nkosi"),
        new(EmailColumn, Aliases: ["E-mail", "Email address"], MaxLength: Person.EmailMaxLength, Example: "thandi@example.com"),
        new(PhoneColumn, Aliases: ["Mobile", "Cell"], MaxLength: Person.PhoneMaxLength, Example: "+27 82 555 0100"),
        new(JobTitleColumn, Aliases: ["Title", "Position", "Role"], MaxLength: Person.JobTitleMaxLength, Example: "Payroll officer"),
        new(DepartmentColumn, Aliases: ["Dept"], Example: "Finance"),
        new(StartDateColumn, ColumnType.Date, Aliases: ["Started", "Start"], Example: "2026-02-01"),
        new(SkillsColumn, Aliases: ["Skill"], Example: "Data analysis, First aid"),
    ];

    public override async Task<IReadOnlyList<ImportRowError>> ValidateAsync(ImportRow row, ImportContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<ImportRowError>();
        if (row.Text(EmailColumn) is { } email)
        {
            if (!MailAddress.TryCreate(email, out var address) || !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ImportRowError(row.Number, EmailColumn, $"'{email}' is not an email address."));
            }
            else if (!context.Seen(EmailColumn).Add(email.ToLowerInvariant()))
            {
                errors.Add(new ImportRowError(row.Number, EmailColumn, "This email appears earlier in the file."));
            }
        }

        if (row.Text(DepartmentColumn) is { } department && await lookups.FindAsync(LookupLists.People.Department, department, cancellationToken) is null)
        {
            errors.Add(new ImportRowError(row.Number, DepartmentColumn, $"There is no department called '{department}'."));
        }

        foreach (var skill in row.List(SkillsColumn))
        {
            if (await lookups.FindAsync(LookupLists.People.Skill, skill, cancellationToken) is null)
            {
                errors.Add(new ImportRowError(row.Number, SkillsColumn, $"There is no skill called '{skill}'."));
            }
        }

        return errors;
    }

    public override async Task<ValidationResponse> ApplyAsync(ImportRow row, ImportContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var email = row.Text(EmailColumn);
        var department = row.Text(DepartmentColumn) is { } d ? (await lookups.FindAsync(LookupLists.People.Department, d, cancellationToken))?.Code : null;
        var skills = new List<string>();
        foreach (var skill in row.List(SkillsColumn))
        {
            if (await lookups.FindAsync(LookupLists.People.Skill, skill, cancellationToken) is { } found)
            {
                skills.Add(found.Code);
            }
        }

        var existing = email is null
            ? null
            : (await people.SearchAsync(new PersonQuery(email, PageSize: 5), cancellationToken)).Items
                .FirstOrDefault(p => string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return await people.CreateAsync(
                new SavePersonRequest(row.Text(FirstNameColumn)!, row.Text(LastNameColumn)!, Email: email, Phone: row.Text(PhoneColumn),
                    DepartmentCode: department, JobTitle: row.Text(JobTitleColumn), StartDate: row.Date(StartDateColumn), Skills: skills),
                cancellationToken);
        }

        // Updating keeps what the file doesn't mention (preferred name, title, notes...).
        var current = (await people.GetAsync(existing.Id, cancellationToken)).GetDataOrThrow();
        return await people.UpdateAsync(
            existing.Id,
            new SavePersonRequest(
                row.Text(FirstNameColumn)!, row.Text(LastNameColumn)!, current.PreferredName, current.TitleCode, email,
                row.Text(PhoneColumn) ?? current.Phone, current.DateOfBirth, department ?? current.DepartmentCode,
                row.Text(JobTitleColumn) ?? current.JobTitle, row.Date(StartDateColumn) ?? current.StartDate,
                skills.Count > 0 ? skills : current.Skills, current.Notes),
            cancellationToken);
    }
}
