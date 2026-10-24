using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Reference).HasMaxLength(Payment.ReferenceLength).IsUnicode(false).IsRequired();
        builder.HasIndex(p => p.Reference).IsUnique();
        builder.Property(p => p.Gateway).HasMaxLength(Payment.GatewayMaxLength).IsRequired();
        builder.Property(p => p.GatewayReference).HasMaxLength(Payment.GatewayReferenceMaxLength);
        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.Currency).HasMaxLength(Payment.CurrencyLength).IsUnicode(false).IsFixedLength().IsRequired();
        builder.Property(p => p.Description).HasMaxLength(Payment.DescriptionMaxLength).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.BillingCycle).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.FailureReason).HasMaxLength(Payment.ReasonMaxLength);
        builder.Property(p => p.CheckoutUrl).HasMaxLength(Payment.CheckoutUrlMaxLength);
        builder.Property(p => p.RequestedByName).HasMaxLength(Payment.NameMaxLength).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.DateCreated });
        builder.HasIndex(p => new { p.Status, p.ExpiresAt });
        builder.HasOne<Domain.Tenancy.Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Plan>().WithMany().HasForeignKey(p => p.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RecurringPayment>().WithMany().HasForeignKey(p => p.RecurringPaymentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RecurringPaymentConfiguration : IEntityTypeConfiguration<RecurringPayment>
{
    public void Configure(EntityTypeBuilder<RecurringPayment> builder)
    {
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Gateway).HasMaxLength(Payment.GatewayMaxLength).IsRequired();
        builder.Property(r => r.Token).HasMaxLength(RecurringPayment.TokenMaxLength);
        builder.HasIndex(r => new { r.Gateway, r.Token }).IsUnique().HasFilter("[Token] IS NOT NULL");
        builder.Property(r => r.Amount).HasPrecision(18, 2);
        builder.Property(r => r.Currency).HasMaxLength(Payment.CurrencyLength).IsUnicode(false).IsFixedLength().IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.BillingCycle).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.StartedByName).HasMaxLength(Payment.NameMaxLength).IsRequired();
        builder.Property(r => r.CancelReason).HasMaxLength(RecurringPayment.ReasonMaxLength);
        builder.Property(r => r.StopError).HasMaxLength(RecurringPayment.ReasonMaxLength);
        builder.HasIndex(r => new { r.TenantId, r.Status });
        builder.HasOne<Domain.Tenancy.Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Plan>().WithMany().HasForeignKey(r => r.PlanId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentNotificationConfiguration : IEntityTypeConfiguration<PaymentNotification>
{
    public void Configure(EntityTypeBuilder<PaymentNotification> builder)
    {
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Gateway).HasMaxLength(Payment.GatewayMaxLength).IsRequired();
        builder.Property(n => n.EventId).HasMaxLength(PaymentNotification.EventIdMaxLength).IsRequired();
        builder.HasIndex(n => new { n.Gateway, n.EventId }).IsUnique();
        builder.Property(n => n.PaymentReference).HasMaxLength(Payment.ReferenceLength).IsUnicode(false);
        builder.HasIndex(n => n.PaymentReference);
        builder.Property(n => n.Outcome).HasConversion<string>().HasMaxLength(20);
        builder.Property(n => n.Detail).HasMaxLength(PaymentNotification.DetailMaxLength);
    }
}

internal sealed class BillingEventConfiguration : IEntityTypeConfiguration<BillingEvent>
{
    public void Configure(EntityTypeBuilder<BillingEvent> builder)
    {
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.ActorName).HasMaxLength(BillingEvent.NameMaxLength).IsRequired();
        builder.Property(e => e.BillingCycle).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.Currency).HasMaxLength(Payment.CurrencyLength).IsUnicode(false);
        builder.Property(e => e.PaymentReference).HasMaxLength(Payment.ReferenceLength).IsUnicode(false);
        builder.Property(e => e.Gateway).HasMaxLength(BillingEvent.CodeMaxLength);
        builder.Property(e => e.Detail).HasMaxLength(BillingEvent.TextMaxLength);
        builder.Property(e => e.Reason).HasMaxLength(BillingEvent.TextMaxLength);
        builder.Property(e => e.TraceId).HasMaxLength(64).IsUnicode(false);
        builder.HasOne<Domain.Tenancy.Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.TenantId, e.OccurredAt });
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.Property(i => i.Id).ValueGeneratedNever();

        // One run of numbers across the platform, given by InvoiceNumbers; the unique index backs it up.
        builder.Property(i => i.Sequence).ValueGeneratedNever();
        builder.HasIndex(i => i.Sequence).IsUnique();
        builder.Ignore(i => i.Number);
        builder.Ignore(i => i.Subtotal);
        builder.Ignore(i => i.IsTaxInvoice);
        builder.HasIndex(i => i.PaymentId).IsUnique();
        builder.HasOne<Payment>().WithMany().HasForeignKey(i => i.PaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Tenancy.Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(i => new { i.TenantId, i.IssuedAt });
        builder.Property(i => i.PaymentReference).HasMaxLength(Payment.ReferenceLength).IsUnicode(false);
        builder.Property(i => i.Gateway).HasMaxLength(Payment.GatewayMaxLength);
        builder.Property(i => i.SellerName).HasMaxLength(Invoice.NameMaxLength).IsRequired();
        builder.Property(i => i.SellerAddress).HasMaxLength(Invoice.TextMaxLength);
        builder.Property(i => i.VatNumber).HasMaxLength(50);
        builder.Property(i => i.BuyerName).HasMaxLength(Invoice.NameMaxLength).IsRequired();
        builder.Property(i => i.BuyerEmail).HasMaxLength(Invoice.NameMaxLength);
        builder.Property(i => i.BuyerContact).HasMaxLength(Invoice.NameMaxLength);
        builder.Property(i => i.BuyerPhone).HasMaxLength(50);
        builder.Property(i => i.BuyerAddress).HasMaxLength(Invoice.TextMaxLength);
        builder.Property(i => i.BuyerTaxNumber).HasMaxLength(50);
        builder.Property(i => i.BuyerRegistrationNumber).HasMaxLength(50);
        builder.Property(i => i.Description).HasMaxLength(Payment.DescriptionMaxLength).IsRequired();
        builder.Property(i => i.Currency).HasMaxLength(Payment.CurrencyLength).IsUnicode(false);
        builder.Property(i => i.Total).HasPrecision(18, 2);
        builder.Property(i => i.VatAmount).HasPrecision(18, 2);
        builder.Property(i => i.VatRatePercent).HasPrecision(5, 2);
        builder.Property(i => i.Footer).HasMaxLength(Invoice.TextMaxLength);
        builder.Property(i => i.LastSentTo).HasMaxLength(Invoice.NameMaxLength);
    }
}
