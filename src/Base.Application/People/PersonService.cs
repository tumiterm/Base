using System.Linq.Expressions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Common.Validation;
using Base.Application.Files;
using Base.Application.MasterData;
using Base.Domain.Files;
using Base.Domain.People;
using FluentValidation;

namespace Base.Application.People;

/// <summary>
/// The people directory. This is the reference use-case service: every operation validates, checks lookups, stages
/// through repositories and commits once, and returns a <see cref="ValidationResponse"/>. Callers gate with
/// <c>People.View</c> / <c>People.Manage</c>.
/// </summary>
public interface IPersonService
{
    Task<PagedResult<PersonSummary>> SearchAsync(PersonQuery query, CancellationToken cancellationToken = default);

    /// <summary>Counts per status for the current filters other than status.</summary>
    Task<PersonCounts> CountAsync(PersonQuery query, CancellationToken cancellationToken = default);

    Task<ValidationResponse<PersonDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ValidationResponse<Guid>> CreateAsync(SavePersonRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> UpdateAsync(Guid id, SavePersonRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ChangeStatusAsync(Guid id, PersonStatus status, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes; the audit trail keeps their history.</summary>
    Task<ValidationResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ValidationResponse> SetAvatarAsync(Guid id, Stream content, string fileName, CancellationToken cancellationToken = default);

    Task<ValidationResponse> RemoveAvatarAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The photo, for anyone who may see the person (not only its uploader).</summary>
    Task<ValidationResponse<FileDownload>> OpenAvatarAsync(Guid id, CancellationToken cancellationToken = default);
}

internal sealed class PersonService(
    IUnitOfWork unitOfWork,
    ILookupService lookups,
    FileService files,
    IValidator<SavePersonRequest> validator) : IPersonService
{
    private const string AvatarCategory = "avatar";
    private const long MaxAvatarBytes = 1024 * 1024;
    private const string NotFoundMessage = "Person not found.";
    private static readonly string[] AvatarExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    public Task<PagedResult<PersonSummary>> SearchAsync(PersonQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var status = query.Status;
        return unitOfWork.Repository<Person>().GetPagedAsync(
            new PageRequest(query.Page, query.PageSize),
            p => new PersonSummary(
                p.Id, (p.PreferredName ?? p.FirstName) + " " + p.LastName, p.FirstName, p.LastName, p.Email, p.JobTitle,
                p.DepartmentCode, p.Status, p.StartDate, p.Skills, p.AvatarFileId != null, p.DateModified, p.Phone),
            q => Order(q, query.Sort, query.Descending),
            And(Filter(query), p => status == null || p.Status == status),
            cancellationToken);
    }

    public async Task<PersonCounts> CountAsync(PersonQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var statuses = await unitOfWork.Repository<Person>().GetAllAsync(p => p.Status, Filter(query), cancellationToken: cancellationToken);
        return new PersonCounts(
            statuses.Count(s => s == PersonStatus.Active),
            statuses.Count(s => s == PersonStatus.OnLeave),
            statuses.Count(s => s == PersonStatus.Left));
    }

    public async Task<ValidationResponse<PersonDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var person = await unitOfWork.Repository<Person>().GetAsync(p => p.Id == id, asNoTracking: true, cancellationToken: cancellationToken);
        return person is null ? ValidationResponse.NotFound(NotFoundMessage).As<PersonDetails>() : ToDetails(person);
    }

    public async Task<ValidationResponse<Guid>> CreateAsync(SavePersonRequest request, CancellationToken cancellationToken = default)
    {
        var valid = await ValidateAsync(request, existing: null, cancellationToken);
        if (valid.IsError)
        {
            return valid.As<Guid>();
        }

        var person = new Person(request.FirstName, request.LastName);
        Apply(person, request);
        await unitOfWork.Repository<Person>().AddAsync(person, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(person.Id, $"{person.DisplayName} added.");
    }

    public async Task<ValidationResponse> UpdateAsync(Guid id, SavePersonRequest request, CancellationToken cancellationToken = default)
    {
        var person = await unitOfWork.Repository<Person>().GetByIdAsync(id, cancellationToken);
        if (person is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage);
        }

        var valid = await ValidateAsync(request, person, cancellationToken);
        if (valid.IsError)
        {
            return valid;
        }

        Apply(person, request);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("Saved.");
    }

    public async Task<ValidationResponse> ChangeStatusAsync(Guid id, PersonStatus status, CancellationToken cancellationToken = default)
    {
        var person = await unitOfWork.Repository<Person>().GetByIdAsync(id, cancellationToken);
        if (person is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage);
        }

        if (person.Status == PersonStatus.Left && status == PersonStatus.OnLeave)
        {
            return ValidationResponse.Failure("Someone who has left can't go on leave; mark them active first.", "person.status");
        }

        person.ChangeStatus(status);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(status switch
        {
            PersonStatus.Left => $"{person.DisplayName} is marked as left.",
            PersonStatus.OnLeave => $"{person.DisplayName} is on leave.",
            _ => $"{person.DisplayName} is active.",
        });
    }

    public async Task<ValidationResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.Repository<Person>();
        var person = await repository.GetByIdAsync(id, cancellationToken);
        if (person is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage);
        }

        repository.Remove(person);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success($"{person.DisplayName} removed.");
    }

    public async Task<ValidationResponse> SetAvatarAsync(Guid id, Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        var person = await unitOfWork.Repository<Person>().GetByIdAsync(id, cancellationToken);
        if (person is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage);
        }

        // SVG is refused: it can carry script. The photo is served to everyone who may see the person.
        var staged = await files.StageAsync(new FileUploadRequest(content, fileName, null, AvatarCategory), null, AvatarExtensions, replaceAllowList: true, cancellationToken, MaxAvatarBytes);
        if (!staged.IsSuccess)
        {
            return staged;
        }

        var previous = person.AvatarFileId;
        person.ChangeAvatar(staged.Data!.File.Id);
        await staged.Data.SaveOrDiscardAsync(unitOfWork, cancellationToken);
        await RemoveFileAsync(previous, cancellationToken);
        return ValidationResponse.Success("Photo updated.");
    }

    public async Task<ValidationResponse> RemoveAvatarAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var person = await unitOfWork.Repository<Person>().GetByIdAsync(id, cancellationToken);
        if (person is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage);
        }

        var previous = person.AvatarFileId;
        person.ChangeAvatar(null);
        await unitOfWork.SaveAsync(cancellationToken);
        await RemoveFileAsync(previous, cancellationToken);
        return ValidationResponse.Success("Photo removed.");
    }

    public async Task<ValidationResponse<FileDownload>> OpenAvatarAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var fileId = await unitOfWork.Repository<Person>().GetAsync(p => p.Id == id, p => p.AvatarFileId, cancellationToken);
        var file = fileId is { } stored
            ? await unitOfWork.Repository<StoredFile>().GetAsync(f => f.Id == stored, asNoTracking: true, cancellationToken: cancellationToken)
            : null;
        return file is null ? ValidationResponse.NotFound("No photo.").As<FileDownload>() : await files.OpenAsync(file, file.OriginalFileName, cancellationToken);
    }

    private static Expression<Func<Person, bool>> Filter(PersonQuery query)
    {
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var department = string.IsNullOrWhiteSpace(query.Department) ? null : query.Department.Trim();
        var skill = string.IsNullOrWhiteSpace(query.Skill) ? null : query.Skill.Trim();

        // The Tenant and SoftDelete filters apply on top of this.
        return p => (search == null || p.FirstName.Contains(search) || p.LastName.Contains(search) || (p.PreferredName != null && p.PreferredName.Contains(search))
                || (p.Email != null && p.Email.Contains(search)) || (p.JobTitle != null && p.JobTitle.Contains(search)))
            && (department == null || p.DepartmentCode == department)
            && (skill == null || p.Skills.Contains(skill));
    }

    private static Expression<Func<Person, bool>> And(Expression<Func<Person, bool>> left, Expression<Func<Person, bool>> right)
    {
        var parameter = left.Parameters[0];
        var body = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<Person, bool>>(Expression.AndAlso(left.Body, body), parameter);
    }

    private static IOrderedQueryable<Person> Order(IQueryable<Person> people, PersonSort sort, bool descending) => (sort, descending) switch
    {
        (PersonSort.Department, false) => people.OrderBy(p => p.DepartmentCode).ThenBy(p => p.LastName).ThenBy(p => p.Id),
        (PersonSort.Department, true) => people.OrderByDescending(p => p.DepartmentCode).ThenBy(p => p.LastName).ThenBy(p => p.Id),
        (PersonSort.StartDate, false) => people.OrderBy(p => p.StartDate).ThenBy(p => p.LastName).ThenBy(p => p.Id),
        (PersonSort.StartDate, true) => people.OrderByDescending(p => p.StartDate).ThenBy(p => p.LastName).ThenBy(p => p.Id),
        (PersonSort.Updated, false) => people.OrderBy(p => p.DateModified).ThenBy(p => p.Id),
        (PersonSort.Updated, true) => people.OrderByDescending(p => p.DateModified).ThenBy(p => p.Id),
        (_, true) => people.OrderByDescending(p => p.LastName).ThenByDescending(p => p.FirstName).ThenBy(p => p.Id),
        _ => people.OrderBy(p => p.LastName).ThenBy(p => p.FirstName).ThenBy(p => p.Id),
    };

    private async Task<ValidationResponse> ValidateAsync(SavePersonRequest request, Person? existing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shape = await validator.ValidateToResponseAsync(request, cancellationToken);
        if (shape.IsError)
        {
            return shape;
        }

        // A value that was hidden later stays valid on the records that already hold it.
        var checks = new[]
        {
            await lookups.EnsureValidAsync(LookupLists.People.Title, [request.TitleCode], nameof(request.TitleCode), [existing?.TitleCode], cancellationToken),
            await lookups.EnsureValidAsync(LookupLists.People.Department, [request.DepartmentCode], nameof(request.DepartmentCode), [existing?.DepartmentCode], cancellationToken),
            await lookups.EnsureValidAsync(LookupLists.People.Skill, request.Skills ?? [], nameof(request.Skills), existing?.Skills, cancellationToken),
        };

        var errors = checks.SelectMany(c => c.Errors).ToList();
        if (errors.Count > 0)
        {
            return ValidationResponse.Failure(errors);
        }

        var email = request.Email?.Trim().ToLowerInvariant();
        var existingId = existing?.Id;
        if (email is { Length: > 0 } && await unitOfWork.Repository<Person>().ExistsAsync(p => p.Email == email && p.Id != existingId, cancellationToken))
        {
            return ValidationResponse.Failure(new ValidationError(nameof(request.Email), "Someone else already has this email.", "person.duplicate"));
        }

        return ValidationResponse.Success();
    }

    private static void Apply(Person person, SavePersonRequest request)
    {
        person.Rename(request.FirstName, request.LastName, request.PreferredName);
        person.ChangeTitle(request.TitleCode);
        person.ChangeContact(request.Email, request.Phone);
        person.ChangeDateOfBirth(request.DateOfBirth);
        person.ChangeRole(request.DepartmentCode, request.JobTitle, request.StartDate);
        person.ChangeSkills(request.Skills);
        person.ChangeNotes(request.Notes);
    }

    private async Task RemoveFileAsync(Guid? fileId, CancellationToken cancellationToken)
    {
        if (fileId is not { } id)
        {
            return;
        }

        var file = await unitOfWork.Repository<StoredFile>().GetByIdAsync(id, cancellationToken);
        if (file is not null)
        {
            unitOfWork.Repository<StoredFile>().Remove(file);
            await unitOfWork.SaveAsync(cancellationToken);
        }
    }

    private static PersonDetails ToDetails(Person p) => new(
        p.Id, p.DisplayName, p.FirstName, p.LastName, p.PreferredName, p.TitleCode, p.Email, p.Phone, p.DateOfBirth,
        p.DepartmentCode, p.JobTitle, p.StartDate, p.Status, p.Skills, p.Notes, p.AvatarFileId is not null,
        p.DateCreated, p.UserCreated, p.DateModified, p.UserModified);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
