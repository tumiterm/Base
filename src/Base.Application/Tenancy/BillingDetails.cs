using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Common.Validation;
using Base.Application.Security;
using Base.Domain.Payments;
using Base.Domain.Tenancy;
using FluentValidation;

namespace Base.Application.Tenancy;

/// <summary>
/// Who and where an organisation's invoices are made out to. <see cref="Email"/> is the tenant's contact email, which
/// also receives subscription and payment notices.
/// </summary>
/// <param name="LegalName">The registered name, when it isn't the organisation's name.</param>
/// <param name="Address">One part per line.</param>
public sealed record BillingDetails(
    string? LegalName = null,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null,
    string? Address = null,
    string? TaxNumber = null,
    string? RegistrationNumber = null)
{
    public static BillingDetails Of(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return new(tenant.LegalName, tenant.BillingContactName, tenant.ContactEmail, tenant.Phone, tenant.Address, tenant.TaxNumber, tenant.RegistrationNumber);
    }

    /// <summary>True when invoices would show the bare organisation name.</summary>
    public bool IsEmpty => LegalName is null && ContactName is null && Phone is null && Address is null && TaxNumber is null && RegistrationNumber is null;

    internal void ApplyTo(Tenant tenant)
    {
        tenant.ChangeContact(Email);
        tenant.ChangeBillingDetails(LegalName, ContactName, Phone, Address, TaxNumber, RegistrationNumber);
    }

    internal static InvoiceBuyer BuyerFor(Tenant? tenant) => tenant is null
        ? new InvoiceBuyer("Organisation", null)
        : new InvoiceBuyer(tenant.LegalName ?? tenant.Name, tenant.ContactEmail, tenant.BillingContactName, tenant.Phone, tenant.Address, tenant.TaxNumber, tenant.RegistrationNumber);
}

/// <summary>This organisation's own billing details, kept by its people (<c>Subscription.Pay</c>).</summary>
public interface IBillingDetailsService
{
    /// <summary>Needs <c>Subscription.View</c>.</summary>
    Task<ValidationResponse<BillingDetails>> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Needs <c>Subscription.Pay</c>. Invoices already issued keep the details they were issued with.</summary>
    Task<ValidationResponse> SaveMineAsync(BillingDetails details, CancellationToken cancellationToken = default);
}

internal sealed class BillingDetailsService(IUnitOfWork unitOfWork, ITenantContext tenant, ICurrentUser currentUser, IValidator<BillingDetails> validator)
    : IBillingDetailsService
{
    public async Task<ValidationResponse<BillingDetails>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        if (!tenant.IsResolved || !currentUser.HasPermission(PermissionCodes.Subscription.View))
        {
            return ValidationResponse.Forbidden().As<BillingDetails>();
        }

        var mine = await unitOfWork.Repository<Tenant>().GetByIdAsync(tenant.TenantId, cancellationToken);
        return mine is null ? ValidationResponse.NotFound().As<BillingDetails>() : ValidationResponse.Success(BillingDetails.Of(mine));
    }

    public async Task<ValidationResponse> SaveMineAsync(BillingDetails details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!tenant.IsResolved || !currentUser.HasPermission(PermissionCodes.Subscription.Pay))
        {
            return ValidationResponse.Forbidden();
        }

        var validation = await validator.ValidateToResponseAsync(details, cancellationToken);
        if (validation.IsError)
        {
            return validation;
        }

        var mine = await unitOfWork.Repository<Tenant>().GetByIdAsync(tenant.TenantId, cancellationToken);
        if (mine is null)
        {
            return ValidationResponse.NotFound();
        }

        details.ApplyTo(mine);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("Billing details saved. New invoices use them.");
    }
}

internal sealed class BillingDetailsValidator : AbstractValidator<BillingDetails>
{
    public BillingDetailsValidator()
    {
        RuleFor(d => d.LegalName).MaximumLength(Tenant.BillingNameMaxLength);
        RuleFor(d => d.ContactName).MaximumLength(Tenant.BillingNameMaxLength);
        RuleFor(d => d.Email).MaximumLength(Tenant.ContactEmailMaxLength).EmailAddress().When(d => !string.IsNullOrWhiteSpace(d.Email));
        RuleFor(d => d.Phone).MaximumLength(Tenant.PhoneMaxLength).Matches(@"^[0-9+() .\-]*$").WithMessage("Use digits, spaces and + ( ) - only.");
        RuleFor(d => d.Address).MaximumLength(Tenant.AddressMaxLength);
        RuleFor(d => d.TaxNumber).MaximumLength(Tenant.NumberMaxLength);
        RuleFor(d => d.RegistrationNumber).MaximumLength(Tenant.NumberMaxLength);
    }
}
