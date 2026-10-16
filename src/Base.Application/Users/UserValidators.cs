using Base.Application.Security;
using FluentValidation;

namespace Base.Application.Users;

internal static class UserRules
{
    public const int EmailMaxLength = 256;
    public const int DisplayNameMaxLength = 200;
    public const int RoleNameMaxLength = 100;
    public const int RoleDescriptionMaxLength = 500;
}

internal sealed class InviteUserRequestValidator : AbstractValidator<InviteUserRequest>
{
    public InviteUserRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().MaximumLength(UserRules.EmailMaxLength).EmailAddress();
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(UserRules.DisplayNameMaxLength);
        RuleFor(r => r.Roles).NotNull().Must(r => r.Distinct(StringComparer.OrdinalIgnoreCase).Count() == r.Count)
            .WithMessage("Each role may be selected once.");
    }
}

internal sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(UserRules.DisplayNameMaxLength);
        RuleFor(r => r.Roles).NotNull().Must(r => r.Distinct(StringComparer.OrdinalIgnoreCase).Count() == r.Count)
            .WithMessage("Each role may be selected once.");
    }
}

internal sealed class SaveRoleRequestValidator : AbstractValidator<SaveRoleRequest>
{
    public SaveRoleRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(UserRules.RoleNameMaxLength)
            .Matches("^[A-Za-z0-9][A-Za-z0-9 _-]*$").WithMessage("Use letters, digits, spaces, dashes or underscores.");
        RuleFor(r => r.Description).MaximumLength(UserRules.RoleDescriptionMaxLength);
        RuleFor(r => r.Permissions).NotNull();
        RuleForEach(r => r.Permissions).Must(PermissionCatalog.Exists).WithMessage("'{PropertyValue}' is not a known permission.");
    }
}

internal sealed class SetPasswordRequestValidator : AbstractValidator<SetPasswordRequest>
{
    public SetPasswordRequestValidator()
    {
        RuleFor(r => r.UserId).NotEmpty();
        RuleFor(r => r.Token).NotEmpty();
        RuleFor(r => r.NewPassword).NotEmpty();
        RuleFor(r => r.ConfirmPassword).Equal(r => r.NewPassword).WithMessage("The passwords do not match.");
    }
}

internal sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(r => r.CurrentPassword).NotEmpty();
        RuleFor(r => r.NewPassword).NotEmpty().NotEqual(r => r.CurrentPassword).WithMessage("Choose a password you haven't used here.");
        RuleFor(r => r.ConfirmPassword).Equal(r => r.NewPassword).WithMessage("The passwords do not match.");
    }
}
