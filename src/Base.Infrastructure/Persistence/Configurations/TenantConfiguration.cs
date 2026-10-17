using Base.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasIndex(t => t.Code).IsUnique();
        builder.Property(t => t.ContactEmail).HasMaxLength(Tenant.ContactEmailMaxLength);
        builder.Property(t => t.ReleaseVersion).HasMaxLength(Tenant.ReleaseVersionMaxLength).IsRequired().HasDefaultValue(Tenant.NoRelease);
        builder.HasMany(t => t.Domains).WithOne().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Domains).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class TenantDomainConfiguration : IEntityTypeConfiguration<TenantDomain>
{
    public void Configure(EntityTypeBuilder<TenantDomain> builder)
    {
        builder.Property(d => d.Host).HasMaxLength(TenantDomain.HostMaxLength).IsRequired();
        builder.HasIndex(d => d.Host).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class TenantFeatureChoiceConfiguration : IEntityTypeConfiguration<TenantFeatureChoice>
{
    public void Configure(EntityTypeBuilder<TenantFeatureChoice> builder)
    {
        builder.Property(c => c.FeatureCode).HasMaxLength(TenantFeatureChoice.FeatureCodeMaxLength).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.FeatureCode }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}
