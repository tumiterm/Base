using Base.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasIndex(t => t.Code).IsUnique();
        builder.HasMany(t => t.Domains).WithOne().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Domains).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class TenantDomainConfiguration : IEntityTypeConfiguration<TenantDomain>
{
    public void Configure(EntityTypeBuilder<TenantDomain> builder)
    {
        builder.Property(d => d.Host).HasMaxLength(TenantDomain.HostMaxLength).IsRequired();
        builder.HasIndex(d => d.Host).IsUnique();
    }
}
