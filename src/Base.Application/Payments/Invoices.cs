using System.Globalization;
using Base.Application.Branding;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Documents.Generation;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Domain.Payments;
using Base.Domain.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Base.Application.Payments;

public sealed record InvoiceView(
    Guid Id,
    string Number,
    string Title,
    DateTimeOffset IssuedAt,
    DateTimeOffset PaidAt,
    string PaymentReference,
    string Description,
    decimal Total,
    decimal VatAmount,
    string Currency,
    string BuyerName,
    string? BuyerEmail,
    string? LastSentTo,
    DateTimeOffset? LastSentAt,
    IReadOnlyList<string> Formats);

/// <summary>
/// Invoices for paid subscription payments: issued when a payment succeeds (or the first time a paid payment's invoice is
/// asked for, for payments from before invoices), frozen, rendered from the <c>Billing.Invoice</c> template, and
/// emailed on request to any address.
/// </summary>
public interface IInvoiceService
{
    /// <summary>The invoice of one of this organisation's paid payments. Needs <c>Subscription.View</c>.</summary>
    Task<ValidationResponse<InvoiceView>> ForPaymentAsync(string paymentReference, CancellationToken cancellationToken = default);

    /// <summary>This organisation's invoice, by number. Needs <c>Subscription.View</c>.</summary>
    Task<ValidationResponse<InvoiceView>> GetAsync(string number, CancellationToken cancellationToken = default);

    /// <summary>The invoice as a file (<c>html</c>, or <c>pdf</c> where a PDF renderer is configured).</summary>
    Task<ValidationResponse<GeneratedFile>> RenderAsync(string number, string format, CancellationToken cancellationToken = default);

    /// <summary>Emails the invoice to <paramref name="email"/>. Needs <c>Subscription.Pay</c>.</summary>
    Task<ValidationResponse> SendAsync(string number, string email, CancellationToken cancellationToken = default);
}

internal sealed class InvoiceService(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ICurrentUser currentUser,
    InvoiceIssuer issuer,
    DocumentGenerator generator,
    INotificationService notifications,
    IApplicationSettingService settings,
    IEnumerable<IPaymentGateway> gateways,
    IBrandingService branding,
    IPlatformTenant platform,
    IServiceScopeFactory scopes,
    TimeProvider time) : IInvoiceService
{
    public async Task<ValidationResponse<InvoiceView>> ForPaymentAsync(string paymentReference, CancellationToken cancellationToken = default)
    {
        if (!CanView())
        {
            return ValidationResponse.Forbidden().As<InvoiceView>();
        }

        var payment = await unitOfWork.Repository<Payment>().GetAsync(p => p.Reference == paymentReference, cancellationToken: cancellationToken);
        if (payment is not { Status: PaymentStatus.Succeeded })
        {
            return ValidationResponse.NotFound("Only a paid payment has an invoice.").As<InvoiceView>();
        }

        var invoice = await issuer.IssueAsync(payment, cancellationToken);
        return ValidationResponse.Success(View(invoice));
    }

    public async Task<ValidationResponse<InvoiceView>> GetAsync(string number, CancellationToken cancellationToken = default)
    {
        if (!CanView())
        {
            return ValidationResponse.Forbidden().As<InvoiceView>();
        }

        var invoice = await FindAsync(number, cancellationToken);
        return invoice is null ? ValidationResponse.NotFound("No such invoice.").As<InvoiceView>() : ValidationResponse.Success(View(invoice));
    }

    public async Task<ValidationResponse<GeneratedFile>> RenderAsync(string number, string format, CancellationToken cancellationToken = default)
    {
        if (!CanView())
        {
            return ValidationResponse.Forbidden().As<GeneratedFile>();
        }

        var invoice = await FindAsync(number, cancellationToken);
        if (invoice is null)
        {
            return ValidationResponse.NotFound("No such invoice.").As<GeneratedFile>();
        }

        return await generator.RenderSystemAsync(DocumentTemplates.Invoice, format, await DataAsync(invoice, cancellationToken), invoice.Number, cancellationToken);
    }

    public async Task<ValidationResponse> SendAsync(string number, string email, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Subscription.Pay) || !tenant.IsResolved)
        {
            return ValidationResponse.Forbidden();
        }

        var address = email?.Trim() ?? string.Empty;
        if (!System.Net.Mail.MailAddress.TryCreate(address, out var parsed) || !string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResponse.Failure(new ValidationError("email", "Enter one email address, like accounts@example.com.", "email"));
        }

        var invoice = await FindAsync(number, cancellationToken);
        if (invoice is null)
        {
            return ValidationResponse.NotFound("No such invoice.");
        }

        var data = await DataAsync(invoice, cancellationToken);
        await notifications.SendAsync(
            new Notification(
                NotificationTemplates.Subscriptions.Invoice,
                NotificationRecipient.ToEmail(address, null),
                new
                {
                    data.InvoiceTitle,
                    data.InvoiceNumber,
                    data.IssuedOn,
                    data.SellerName,
                    data.BuyerName,
                    data.Description,
                    data.Total,
                    data.VatLabel,
                    data.VatAmount,
                    data.PaidOn,
                    data.PaymentReference,
                    InvoicePath = "/subscription/invoices/" + invoice.Number,
                }),
            cancellationToken);
        invoice.Sent(address, time.GetUtcNow());
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success($"Invoice {invoice.Number} sent to {address}.");
    }

    private bool CanView() => currentUser.HasPermission(PermissionCodes.Subscription.View) && tenant.IsResolved;

    private async Task<Invoice?> FindAsync(string number, CancellationToken cancellationToken)
    {
        if (number is null || !number.StartsWith(Invoice.NumberPrefix, StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(number.AsSpan(Invoice.NumberPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return null;
        }

        return await unitOfWork.Repository<Invoice>().GetAsync(i => i.Sequence == sequence, cancellationToken: cancellationToken);
    }

    private InvoiceView View(Invoice i) => new(
        i.Id, i.Number, i.IsTaxInvoice ? "Tax invoice" : "Invoice", i.IssuedAt, i.PaidAt, i.PaymentReference, i.Description, i.Total, i.VatAmount, i.Currency,
        i.BuyerName, i.BuyerEmail, i.LastSentTo, i.LastSentAt, generator.Formats.ToList());

    private async Task<InvoiceData> DataAsync(Invoice i, CancellationToken cancellationToken)
    {
        var zoneId = await settings.GetAsync(SettingCodes.General.DisplayTimeZone, cancellationToken);
        var zone = zoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found) ? found : TimeZoneInfo.Utc;
        string Date(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        string Money(decimal amount) => i.Currency + " " + amount.ToString("N2", CultureInfo.InvariantCulture);
        var rate = i.VatRatePercent.ToString("0.##", CultureInfo.InvariantCulture);
        var (sellerLogo, buyerLogo) = await LogosAsync(cancellationToken);
        string[] numbers =
        [
            .. i.BuyerTaxNumber is null ? Array.Empty<string>() : ["VAT number " + i.BuyerTaxNumber],
            .. i.BuyerRegistrationNumber is null ? Array.Empty<string>() : ["Registration " + i.BuyerRegistrationNumber],
        ];
        return new InvoiceData(
            i.IsTaxInvoice ? "Tax invoice" : "Invoice",
            i.Number,
            Date(i.IssuedAt),
            i.SellerName,
            i.SellerAddress ?? string.Empty,
            i.VatNumber is null ? string.Empty : "VAT number " + i.VatNumber,
            i.BuyerName,
            i.BuyerEmail ?? string.Empty,
            i.BuyerContact is null ? string.Empty : "Attention: " + i.BuyerContact,
            i.BuyerAddress ?? string.Empty,
            i.BuyerPhone ?? string.Empty,
            string.Join(" · ", numbers),
            sellerLogo ?? BlankImage,
            sellerLogo is null ? "logo is-empty" : "logo",
            buyerLogo ?? BlankImage,
            buyerLogo is null ? "buyer-logo is-empty" : "buyer-logo",
            Date(i.PaidAt),
            gateways.FirstOrDefault(g => string.Equals(g.Code, i.Gateway, StringComparison.OrdinalIgnoreCase))?.Name ?? i.Gateway,
            i.PaymentReference,
            i.Description,
            i.IsTaxInvoice ? "Amount excluding VAT" : "Subtotal",
            Money(i.Subtotal),
            i.IsTaxInvoice ? $"VAT ({rate}%)" : "VAT (not VAT registered)",
            Money(i.VatAmount),
            Money(i.Total),
            i.Footer ?? string.Empty);
    }

    /// <summary>
    /// The logos as data URIs, so the document stands alone (preview, PDF, saved file): the platform's own logo as the
    /// seller's, and the organisation's logo beside its name when it has one of its own.
    /// </summary>
    private async Task<(string? Seller, string? Buyer)> LogosAsync(CancellationToken cancellationToken)
    {
        var buyer = await DataUriAsync(branding, cancellationToken);
        var platformId = await platform.GetIdAsync(cancellationToken);
        if (platformId == tenant.TenantId)
        {
            return (buyer, null);
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(platformId, null);
        var seller = await DataUriAsync(scope.ServiceProvider.GetRequiredService<IBrandingService>(), cancellationToken);
        return (seller, string.Equals(seller, buyer, StringComparison.Ordinal) ? null : buyer);
    }

    private static async Task<string?> DataUriAsync(IBrandingService source, CancellationToken cancellationToken)
    {
        var logo = await source.OpenLogoAsync(cancellationToken);
        if (logo is null)
        {
            return null;
        }

        await using (logo.Content)
        {
            using var bytes = new MemoryStream();
            await logo.Content.CopyToAsync(bytes, cancellationToken);
            return $"data:{logo.ContentType};base64,{Convert.ToBase64String(bytes.GetBuffer(), 0, (int)bytes.Length)}";
        }
    }

    /// <summary>A transparent pixel for a missing logo (hidden by its class), so the image never shows as broken.</summary>
    private const string BlankImage = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    private sealed record InvoiceData(
        string InvoiceTitle, string InvoiceNumber, string IssuedOn, string SellerName, string SellerAddress, string VatLine, string BuyerName, string BuyerEmail,
        string BuyerContact, string BuyerAddress, string BuyerPhone, string BuyerNumbers, string SellerLogo, string SellerLogoClass, string BuyerLogo,
        string BuyerLogoClass, string PaidOn, string PaidWith, string PaymentReference, string Description, string SubtotalLabel, string Subtotal, string VatLabel, string VatAmount,
        string Total, string Footer);
}

/// <summary>
/// Invoice numbers: one run across the platform without gaps (a database sequence skips numbers it had cached when the
/// server restarts, and ones taken by a failed save).
/// </summary>
public interface IInvoiceNumbers
{
    /// <summary>The next number. Call inside the transaction that saves the invoice: it holds the number until then.</summary>
    Task<long> NextAsync(CancellationToken cancellationToken);
}

/// <summary>Issues a payment's invoice once, frozen with today's seller details and the organisation as buyer.</summary>
internal sealed class InvoiceIssuer(IUnitOfWork unitOfWork, IInvoiceNumbers numbers, IApplicationSettingService settings, TimeProvider time)
    : IDomainEventHandler<PaymentSucceeded>
{
    public async Task HandleAsync(PaymentSucceeded domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (await unitOfWork.Repository<Payment>().GetByIdAsync(domainEvent.PaymentId, cancellationToken) is { Status: PaymentStatus.Succeeded } payment)
        {
            await IssueAsync(payment, cancellationToken);
        }
    }

    public async Task<Invoice> IssueAsync(Payment payment, CancellationToken cancellationToken)
    {
        var invoices = unitOfWork.Repository<Invoice>();
        if (await invoices.GetAsync(i => i.PaymentId == payment.Id, cancellationToken: cancellationToken) is { } existing)
        {
            return existing;
        }

        var owner = await unitOfWork.Repository<Tenant>().GetByIdAsync(payment.TenantId, cancellationToken);
        var seller = await settings.GetAsync(SettingCodes.Invoices.SellerName, cancellationToken);
        if (string.IsNullOrWhiteSpace(seller))
        {
            seller = await settings.GetAsync(SettingCodes.General.ApplicationName, cancellationToken) ?? "Base";
        }

        var rate = decimal.TryParse(await settings.GetAsync(SettingCodes.Invoices.VatRatePercent, cancellationToken), NumberStyles.Number, CultureInfo.InvariantCulture, out var r) ? r : 0m;
        var sellerAddress = await settings.GetAsync(SettingCodes.Invoices.SellerAddress, cancellationToken);
        var vatNumber = await settings.GetAsync(SettingCodes.Invoices.VatNumber, cancellationToken);
        var footer = await settings.GetAsync(SettingCodes.Invoices.Footer, cancellationToken);
        var buyer = Tenancy.BillingDetails.BuyerFor(owner);
        return await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var invoice = new Invoice(payment, await numbers.NextAsync(ct), seller, sellerAddress, vatNumber, rate, buyer, footer, time.GetUtcNow());
                await invoices.AddAsync(invoice, ct);
                await unitOfWork.SaveAsync(ct);
                return invoice;
            },
            cancellationToken);
    }
}
