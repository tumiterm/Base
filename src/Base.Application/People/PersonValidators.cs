using Base.Domain.People;
using FluentValidation;

namespace Base.Application.People;

/// <summary>Shape rules. Lookup codes are checked against the tenant's lists by the service.</summary>
internal sealed class SavePersonRequestValidator : AbstractValidator<SavePersonRequest>
{
    public SavePersonRequestValidator(TimeProvider time)
    {
        RuleFor(r => r.FirstName).NotEmpty().MaximumLength(Person.NameMaxLength);
        RuleFor(r => r.LastName).NotEmpty().MaximumLength(Person.NameMaxLength);
        RuleFor(r => r.PreferredName).MaximumLength(Person.NameMaxLength);
        RuleFor(r => r.Email).MaximumLength(Person.EmailMaxLength).EmailAddress().When(r => !string.IsNullOrWhiteSpace(r.Email));
        RuleFor(r => r.Phone).MaximumLength(Person.PhoneMaxLength).Matches(@"^\+?[0-9 ()-]{6,}$").WithMessage("Enter a phone number, e.g. +27 82 555 0100.")
            .When(r => !string.IsNullOrWhiteSpace(r.Phone));
        RuleFor(r => r.JobTitle).MaximumLength(Person.JobTitleMaxLength);
        RuleFor(r => r.Notes).MaximumLength(Person.NotesMaxLength);
        RuleFor(r => r.Skills).Must(s => s is null || s.Count <= Person.MaxSkills).WithMessage($"Choose at most {Person.MaxSkills} skills.");
        RuleFor(r => r.DateOfBirth).Must(d => d is null || d <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))
            .WithMessage("The date of birth can't be in the future.");
    }
}
