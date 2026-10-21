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
        builder.Property(p => p.RequestedByName).HasMaxLength(Payment.NameMaxLength).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.DateCreated });
        builder.HasIndex(p => new { p.Status, p.ExpiresAt });
        builder.HasOne<Domain.Tenancy.Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Plan>().WithMany().HasForeignKey(p => p.PlanId).OnDelete(DeleteBehavior.Restrict);
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
