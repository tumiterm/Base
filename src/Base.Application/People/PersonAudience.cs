using System.Runtime.CompilerServices;
using Base.Application.Common.Persistence;
using Base.Application.Communications;
using Base.Application.MasterData;
using Base.Application.Security;
using Base.Domain.People;

namespace Base.Application.People;

/// <summary>People as an audience for messages, filtered the way the People list filters them.</summary>
internal sealed class PersonAudience(IPersonService people, ILookupService lookups) : IAudienceSource
{
    private const string StatusFilter = "status";
    private const string DepartmentFilter = "department";
    private const string SkillFilter = "skill";

    public string Code => AudienceCodes.People;

    public string Name => "People";

    public string Description => "Everyone in the people directory, by department, status or skill.";

    public string Icon => "contact";

    public string Permission => PermissionCodes.People.View;

    public IReadOnlyList<AudienceField> Fields { get; } = [new("JobTitle", "Job title"), new("Department", "Department")];

    public async Task<IReadOnlyList<AudienceFilter>> FiltersAsync(CancellationToken cancellationToken = default) =>
    [
        new(StatusFilter, "Status", [new(nameof(PersonStatus.Active), "Active"), new(nameof(PersonStatus.OnLeave), "On leave"), new(nameof(PersonStatus.Left), "Left")]),
        new(DepartmentFilter, "Department", (await lookups.GetAsync(LookupLists.People.Department, cancellationToken: cancellationToken)).Select(i => new AudienceOption(i.Code, i.Name)).ToList()),
        new(SkillFilter, "Skill", (await lookups.GetAsync(LookupLists.People.Skill, cancellationToken: cancellationToken)).Select(i => new AudienceOption(i.Code, i.Name)).ToList()),
    ];

    public async IAsyncEnumerable<AudienceMember> MembersAsync(IReadOnlyDictionary<string, string> filters, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var departments = await DepartmentsAsync(cancellationToken);
        var query = new PersonQuery(
            Status: filters.TryGetValue(StatusFilter, out var status) && Enum.TryParse<PersonStatus>(status, out var parsed) ? parsed : null,
            Department: filters.GetValueOrDefault(DepartmentFilter),
            Skill: filters.GetValueOrDefault(SkillFilter),
            PageSize: PageRequest.MaxPageSize);
        await foreach (var person in AsyncEnumerables.Pages(PageRequest.MaxPageSize, async page => (await people.SearchAsync(query with { Page = page }, cancellationToken)).Items, cancellationToken))
        {
            yield return ToMember(person, departments);
        }
    }

    public async Task<IReadOnlyList<AudienceMember>> FindAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var departments = await DepartmentsAsync(cancellationToken);
        var members = new List<AudienceMember>();
        foreach (var key in keys)
        {
            if (Guid.TryParse(key, out var id) && (await people.GetAsync(id, cancellationToken)).Data is { } person)
            {
                members.Add(new AudienceMember(person.Id.ToString(), person.DisplayName, person.Email, person.Phone, null, Values(person.JobTitle, person.DepartmentCode, departments)));
            }
        }

        return members;
    }

    public async Task<IReadOnlyList<AudienceMember>> SearchAsync(string term, int take, CancellationToken cancellationToken = default)
    {
        var departments = await DepartmentsAsync(cancellationToken);
        var page = await people.SearchAsync(new PersonQuery(Search: term, PageSize: Math.Clamp(take, 1, PageRequest.MaxPageSize)), cancellationToken);
        return page.Items.Select(p => ToMember(p, departments)).ToList();
    }

    private static AudienceMember ToMember(PersonSummary person, IReadOnlyDictionary<string, string> departments) =>
        new(person.Id.ToString(), person.DisplayName, person.Email, person.Phone, null, Values(person.JobTitle, person.DepartmentCode, departments));

    private static Dictionary<string, string?> Values(string? jobTitle, string? departmentCode, IReadOnlyDictionary<string, string> departments) => new(StringComparer.Ordinal)
    {
        ["JobTitle"] = jobTitle,
        ["Department"] = departmentCode is null ? null : departments.GetValueOrDefault(departmentCode, departmentCode),
    };

    private async Task<IReadOnlyDictionary<string, string>> DepartmentsAsync(CancellationToken cancellationToken) =>
        (await lookups.GetAsync(LookupLists.People.Department, includeInactive: true, cancellationToken)).ToDictionary(i => i.Code, i => i.Name, StringComparer.Ordinal);
}
