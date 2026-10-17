using Base.Domain.Common;
using Base.Domain.Tenancy;
using FluentValidation;

namespace Base.Application.Tenancy;

public static class TenantRules
{
    /// <summary>Lower-case letters, digits and dashes; it appears in URLs and object keys.</summary>
    public const string CodePattern = "^[a-z0-9](?:[a-z0-9-]{0,48}[a-z0-9])?$";

    /// <summary>A DNS host name (no scheme, port or path).</summary>
    public const string HostPattern = @"^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$";
}

internal sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(INamedEntity.NameMaxLength);
        RuleFor(r => r.Code).NotEmpty().Matches(TenantRules.CodePattern).WithMessage("Use 1–50 lower-case letters, digits or dashes, starting and ending with a letter or digit.");
        RuleFor(r => r.Host).Matches(TenantRules.HostPattern).WithMessage("Enter a host name such as acme.example.com.").When(r => !string.IsNullOrWhiteSpace(r.Host));
        RuleFor(r => r.AdminEmail).NotEmpty().MaximumLength(Tenant.ContactEmailMaxLength).EmailAddress();
        RuleFor(r => r.AdminName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.BillingCycle).IsInEnum();
    }
}

internal sealed class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    public UpdateTenantRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(INamedEntity.NameMaxLength);
        RuleFor(r => r.ContactEmail).MaximumLength(Tenant.ContactEmailMaxLength).EmailAddress().When(r => !string.IsNullOrWhiteSpace(r.ContactEmail));
    }
}
