using System.Globalization;
using Base.Domain.Common;

namespace Base.Domain.Payments;

/// <summary>
/// The invoice for a paid <see cref="Payment"/>: numbered in sequence across the platform (<see cref="Number"/>) and
/// frozen when issued. Seller, buyer and amounts are copied in, so changing a setting or an organisation's details
/// later never changes an invoice already issued. Amounts include VAT; <see cref="VatAmount"/> is the part that is VAT.
/// </summary>
public sealed class Invoice : ITenantOwned
{
    public const int NameMaxLength = 200;
    public const int TextMaxLength = 500;
    public const string NumberPrefix = "INV-";

    private Invoice()
    {
    }

    public Invoice(
        Payment payment, string sellerName, string? sellerAddress, string? vatNumber, decimal vatRatePercent,
        string buyerName, string? buyerEmail, string? footer, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (payment.Status != PaymentStatus.Succeeded)
        {
            throw new InvalidOperationException("Only a paid payment has an invoice.");
        }

        Id = SequentialGuid.Next();
        TenantId = payment.TenantId;
        PaymentId = payment.Id;
        PaymentReference = payment.Reference;
        Gateway = payment.Gateway;
        IssuedAt = issuedAt;
        PaidAt = payment.CompletedAt ?? issuedAt;
        SellerName = Limit(sellerName, NameMaxLength)!;
        SellerAddress = Limit(sellerAddress, TextMaxLength);
        VatNumber = Limit(vatNumber, 50);
        BuyerName = Limit(buyerName, NameMaxLength)!;
        BuyerEmail = Limit(buyerEmail, NameMaxLength);
        Description = payment.Description;
        Currency = payment.Currency;
        Total = payment.Amount;
        VatRatePercent = string.IsNullOrWhiteSpace(vatNumber) ? 0m : vatRatePercent;
        VatAmount = VatRatePercent <= 0 ? 0m : decimal.Round(Total * VatRatePercent / (100m + VatRatePercent), 2, MidpointRounding.AwayFromZero);
        Footer = Limit(footer, TextMaxLength);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; set; }

    /// <summary>Given by the database from a sequence, on insert.</summary>
    public long Sequence { get; private set; }

    public string Number => NumberPrefix + Sequence.ToString("D6", CultureInfo.InvariantCulture);

    public Guid PaymentId { get; private set; }

    public string PaymentReference { get; private set; } = string.Empty;

    public string Gateway { get; private set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset PaidAt { get; private set; }

    public string SellerName { get; private set; } = string.Empty;

    public string? SellerAddress { get; private set; }

    public string? VatNumber { get; private set; }

    public string BuyerName { get; private set; } = string.Empty;

    public string? BuyerEmail { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;

    /// <summary>What was paid, VAT included.</summary>
    public decimal Total { get; private set; }

    public decimal VatRatePercent { get; private set; }

    public decimal VatAmount { get; private set; }

    public decimal Subtotal => Total - VatAmount;

    public bool IsTaxInvoice => VatNumber is not null;

    public string? Footer { get; private set; }

    /// <summary>When it was last emailed, and to whom.</summary>
    public DateTimeOffset? LastSentAt { get; private set; }

    public string? LastSentTo { get; private set; }

    public void Sent(string email, DateTimeOffset now)
    {
        LastSentTo = Limit(email, NameMaxLength);
        LastSentAt = now;
    }

    private static string? Limit(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
