using Base.Domain.Auditing;
using Base.Domain.Common;

namespace Base.Domain.People;

public enum PersonStatus
{
    Active = 0,
    OnLeave = 1,
    Left = 2,
}

/// <summary>
/// The sample entity: a person in the organisation. It is the reference for new modules, so it uses each convention
/// once: tenant-owned, lookup codes (title, department, skills), personal data redacted in the audit trail, a status
/// with rules, and an avatar stored through the file service.
/// </summary>
public sealed class Person : EntityBase, ITenantOwned
{
    public const int NameMaxLength = 100;
    public const int EmailMaxLength = 256;
    public const int PhoneMaxLength = 30;
    public const int JobTitleMaxLength = 100;
    public const int NotesMaxLength = 2000;
    public const int MaxSkills = 20;
    public const int LookupCodeMaxLength = MasterData.LookupValue.CodeMaxLength;

    private List<string> _skills = [];

    private Person()
    {
    }

    public Person(string firstName, string lastName)
    {
        Id = SequentialGuid.Next();
        Status = PersonStatus.Active;
        Rename(firstName, lastName, preferredName: null);
    }

    public Guid TenantId { get; set; }

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    /// <summary>What colleagues call them, if not their first name.</summary>
    public string? PreferredName { get; private set; }

    /// <summary>Lookup code from the People.Title list.</summary>
    public string? TitleCode { get; private set; }

    public string? Email { get; private set; }

    [AuditRedacted]
    public string? Phone { get; private set; }

    [AuditRedacted]
    public DateOnly? DateOfBirth { get; private set; }

    /// <summary>Lookup code from the People.Department list.</summary>
    public string? DepartmentCode { get; private set; }

    public string? JobTitle { get; private set; }

    public DateOnly? StartDate { get; private set; }

    public PersonStatus Status { get; private set; }

    /// <summary>Lookup codes from the People.Skill list.</summary>
    public IReadOnlyList<string> Skills => _skills;

    public string? Notes { get; private set; }

    /// <summary>The stored file holding their photo, if any.</summary>
    public Guid? AvatarFileId { get; private set; }

    public string DisplayName => $"{PreferredName ?? FirstName} {LastName}";

    public void Rename(string firstName, string lastName, string? preferredName)
    {
        FirstName = Required(firstName, NameMaxLength, nameof(firstName));
        LastName = Required(lastName, NameMaxLength, nameof(lastName));
        PreferredName = Optional(preferredName, NameMaxLength, nameof(preferredName));
    }

    public void ChangeTitle(string? titleCode) => TitleCode = Optional(titleCode, LookupCodeMaxLength, nameof(titleCode));

    public void ChangeContact(string? email, string? phone)
    {
        Email = Optional(email, EmailMaxLength, nameof(email))?.ToLowerInvariant();
        Phone = Optional(phone, PhoneMaxLength, nameof(phone));
    }

    public void ChangeDateOfBirth(DateOnly? dateOfBirth) => DateOfBirth = dateOfBirth;

    public void ChangeRole(string? departmentCode, string? jobTitle, DateOnly? startDate)
    {
        DepartmentCode = Optional(departmentCode, LookupCodeMaxLength, nameof(departmentCode));
        JobTitle = Optional(jobTitle, JobTitleMaxLength, nameof(jobTitle));
        StartDate = startDate;
    }

    public void ChangeSkills(IEnumerable<string>? skillCodes)
    {
        var skills = (skillCodes ?? [])
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (skills.Count > MaxSkills)
        {
            throw new ArgumentException($"At most {MaxSkills} skills.", nameof(skillCodes));
        }

        _skills = skills;
    }

    public void ChangeNotes(string? notes) => Notes = Optional(notes, NotesMaxLength, nameof(notes));

    /// <summary>Someone who has left can only come back as active; leave needs an active person.</summary>
    public void ChangeStatus(PersonStatus status)
    {
        if (status == PersonStatus.OnLeave && Status == PersonStatus.Left)
        {
            throw new InvalidOperationException("Someone who has left can't go on leave; mark them active first.");
        }

        Status = status;
    }

    public void ChangeAvatar(Guid? fileId) => AvatarFileId = fileId;

    private static string Required(string value, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return Optional(value, maxLength, name)!;
    }

    private static string? Optional(string? value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : throw new ArgumentException($"At most {maxLength} characters.", name);
    }
}
