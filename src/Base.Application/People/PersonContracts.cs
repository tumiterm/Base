using Base.Application.Common.Persistence;
using Base.Domain.People;

namespace Base.Application.People;

public enum PersonSort
{
    Name = 0,
    Department = 1,
    StartDate = 2,
    Updated = 3,
}

/// <summary>Filters for the people list. Every filter is optional.</summary>
public sealed record PersonQuery(
    string? Search = null,
    PersonStatus? Status = null,
    string? Department = null,
    string? Skill = null,
    PersonSort Sort = PersonSort.Name,
    bool Descending = false,
    int Page = 1,
    int PageSize = PageRequest.DefaultPageSize);

/// <summary>A row in the people list. Lookup values are codes; views label them through <c>ILookupService</c>.</summary>
public sealed record PersonSummary(
    Guid Id,
    string DisplayName,
    string FirstName,
    string LastName,
    string? Email,
    string? JobTitle,
    string? DepartmentCode,
    PersonStatus Status,
    DateOnly? StartDate,
    IReadOnlyList<string> Skills,
    bool HasAvatar,
    DateTimeOffset DateModified);

public sealed record PersonDetails(
    Guid Id,
    string DisplayName,
    string FirstName,
    string LastName,
    string? PreferredName,
    string? TitleCode,
    string? Email,
    string? Phone,
    DateOnly? DateOfBirth,
    string? DepartmentCode,
    string? JobTitle,
    DateOnly? StartDate,
    PersonStatus Status,
    IReadOnlyList<string> Skills,
    string? Notes,
    bool HasAvatar,
    DateTimeOffset DateCreated,
    string? UserCreated,
    DateTimeOffset DateModified,
    string? UserModified);

/// <summary>Everything an editor can change at once (create and update share it).</summary>
public sealed record SavePersonRequest(
    string FirstName,
    string LastName,
    string? PreferredName = null,
    string? TitleCode = null,
    string? Email = null,
    string? Phone = null,
    DateOnly? DateOfBirth = null,
    string? DepartmentCode = null,
    string? JobTitle = null,
    DateOnly? StartDate = null,
    IReadOnlyList<string>? Skills = null,
    string? Notes = null);

/// <summary>How many people are in each status, for the list's filter.</summary>
public sealed record PersonCounts(int Active, int OnLeave, int Left)
{
    public int All => Active + OnLeave + Left;

    public int Of(PersonStatus? status) => status switch
    {
        PersonStatus.Active => Active,
        PersonStatus.OnLeave => OnLeave,
        PersonStatus.Left => Left,
        _ => All,
    };
}
