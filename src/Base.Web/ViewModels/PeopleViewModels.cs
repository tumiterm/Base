using Base.Application.Auditing;
using Base.Application.Common.Persistence;
using Base.Application.MasterData;
using Base.Application.People;
using Base.Domain.People;
using Base.Web.Formatting;

namespace Base.Web.ViewModels;

public sealed record PeopleListViewModel(PagedResult<PersonSummary> People, PersonCounts Counts, PersonQuery Query, LookupLabels Labels, PersonForm NewPerson);

public enum PersonTab
{
    Profile = 0,
    Documents = 1,
    History = 2,
}

public sealed record PersonPageViewModel(
    PersonDetails Person,
    PersonForm Form,
    PersonTab Tab,
    LookupLabels Labels,
    PagedResult<AuditRecord>? History,
    bool Editing);

/// <summary>The person form, bound from posts; mirrors <see cref="SavePersonRequest"/>.</summary>
public sealed class PersonForm
{
    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string? PreferredName { get; set; }

    public string? TitleCode { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public string? DepartmentCode { get; set; }

    public string? JobTitle { get; set; }

    public DateOnly? StartDate { get; set; }

    public IList<string> Skills { get; set; } = [];

    public string? Notes { get; set; }

    public static PersonForm From(PersonDetails p) => new()
    {
        FirstName = p.FirstName,
        LastName = p.LastName,
        PreferredName = p.PreferredName,
        TitleCode = p.TitleCode,
        Email = p.Email,
        Phone = p.Phone,
        DateOfBirth = p.DateOfBirth,
        DepartmentCode = p.DepartmentCode,
        JobTitle = p.JobTitle,
        StartDate = p.StartDate,
        Skills = [.. p.Skills],
        Notes = p.Notes,
    };

    public SavePersonRequest ToRequest() => new(
        FirstName, LastName, PreferredName, TitleCode, Email, Phone, DateOfBirth, DepartmentCode, JobTitle, StartDate, [.. Skills], Notes);
}

public sealed record MasterDataViewModel(IReadOnlyList<LookupListSummary> Lists, LookupListSummary Selected, IReadOnlyList<LookupItem> Values);

public static class PersonDisplay
{
    public static string Status(PersonStatus status) => status switch
    {
        PersonStatus.OnLeave => "On leave",
        _ => status.ToString(),
    };

    public static string Tone(PersonStatus status) => status switch
    {
        PersonStatus.Active => "success",
        PersonStatus.OnLeave => "warning",
        _ => "secondary",
    };

    /// <summary>Audit property names as people read them.</summary>
    public static string Field(string property) => property switch
    {
        nameof(Person.FirstName) => "First name",
        nameof(Person.LastName) => "Last name",
        nameof(Person.PreferredName) => "Preferred name",
        nameof(Person.TitleCode) => "Title",
        nameof(Person.DateOfBirth) => "Date of birth",
        nameof(Person.DepartmentCode) => "Department",
        nameof(Person.JobTitle) => "Job title",
        nameof(Person.StartDate) => "Start date",
        nameof(Person.AvatarFileId) => "Photo",
        _ => property,
    };

    /// <summary>The lookup list a property's codes come from, so history shows names.</summary>
    public static string? ListOf(string property) => property switch
    {
        nameof(Person.TitleCode) => LookupLists.People.Title,
        nameof(Person.DepartmentCode) => LookupLists.People.Department,
        nameof(Person.Skills) => LookupLists.People.Skill,
        _ => null,
    };
}
