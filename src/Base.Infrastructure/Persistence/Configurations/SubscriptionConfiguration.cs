using Base.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasIndex(p => p.Code).IsUnique();
        builder.Property(p => p.Description).HasMaxLength(Plan.DescriptionMaxLength);
        builder.Property(p => p.Currency).HasMaxLength(Plan.CurrencyLength).IsUnicode(false).IsFixedLength().IsRequired();
        builder.HasMany(p => p.Entitlements).WithOne().HasForeignKey(e => e.PlanId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Entitlements).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PlanEntitlementConfiguration : IEntityTypeConfiguration<PlanEntitlement>
{
    public void Configure(EntityTypeBuilder<PlanEntitlement> builder)
    {
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Code).HasMaxLength(PlanEntitlement.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.HasIndex(e => new { e.PlanId, e.Code }).IsUnique();
    }
}

internal sealed class TenantSubscriptionConfiguration : IEntityTypeConfiguration<TenantSubscription>
{
    public void Configure(EntityTypeBuilder<TenantSubscription> builder)
    {
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.BillingCycle).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.StatusReason).HasMaxLength(TenantSubscription.ReasonMaxLength);
        builder.Property(s => s.ExternalReference).HasMaxLength(TenantSubscription.ExternalReferenceMaxLength);
        builder.HasOne<Domain.Tenancy.Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Plan>().WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(s => s.ScheduledBillingCycle).HasConversion<string>().HasMaxLength(20);
        builder.HasOne<Plan>().WithMany().HasForeignKey(s => s.ScheduledPlanId).OnDelete(DeleteBehavior.Restrict);

        // One live subscription per tenant; the lifecycle job scans by status and date.
        builder.HasIndex(s => s.TenantId).IsUnique().HasFilter("[IsDeleted] = 0").HasDatabaseName("IX_TenantSubscriptions_TenantId_Live");
        builder.HasIndex(s => new { s.Status, s.CurrentPeriodEnd });
    }
}
