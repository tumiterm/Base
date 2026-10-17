using Base.Domain.Common;
using Base.Domain.Subscriptions;
using FluentValidation;

namespace Base.Application.Subscriptions;

internal sealed class SavePlanRequestValidator : AbstractValidator<SavePlanRequest>
{
    public SavePlanRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().MaximumLength(INamedEntity.CodeMaxLength)
            .Matches("^[a-z0-9][a-z0-9-]*$").WithMessage("Use lower-case letters, digits and dashes.");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(INamedEntity.NameMaxLength);
        RuleFor(r => r.Description).MaximumLength(Plan.DescriptionMaxLength);
        RuleFor(r => r.Currency).NotEmpty().Length(Plan.CurrencyLength).Matches("^[A-Za-z]{3}$").WithMessage("Use a three-letter currency code, e.g. ZAR.");
        RuleFor(r => r.MonthlyPrice).GreaterThanOrEqualTo(0);
        RuleFor(r => r.AnnualPrice).GreaterThanOrEqualTo(0);
        RuleFor(r => r.TrialDays).InclusiveBetween(0, Plan.MaxTrialDays);
        RuleFor(r => r.Entitlements).NotNull();
        RuleForEach(r => r.Entitlements).Must(e => EntitlementCatalog.Find(e.Key) is not null).WithMessage("Unknown entitlement.")
            .Must(e => EntitlementCatalog.Find(e.Key)?.Kind != EntitlementKind.Feature || e.Value is 0 or 1).WithMessage("A feature is on (1) or off (0).")
            .Must(e => e.Value is null or >= 0).WithMessage("Limits can't be negative.");
    }
}
