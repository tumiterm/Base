using Base.Domain.Flags;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class FeatureFlagStateConfiguration : IEntityTypeConfiguration<FeatureFlagState>
{
    public void Configure(EntityTypeBuilder<FeatureFlagState> builder)
    {
        builder.ToTable("FeatureFlags");
        builder.Property(f => f.Code).HasMaxLength(FeatureFlagState.CodeMaxLength).IsRequired();
        builder.Property(f => f.Mode).HasConversion<string>().HasMaxLength(20);
        builder.PrimitiveCollection(f => f.TenantIds).HasMaxLength(-1).HasField("_tenantIds");
        builder.HasIndex(f => f.Code).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}
