using Base.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationSettingConfiguration : IEntityTypeConfiguration<ApplicationSetting>
{
    public void Configure(EntityTypeBuilder<ApplicationSetting> builder)
    {
        builder.Property(s => s.Code).HasMaxLength(ApplicationSetting.CodeMaxLength).IsRequired();
        builder.Property(s => s.Value).HasMaxLength(ApplicationSetting.ValueMaxLength);
        builder.Property(s => s.ProtectedValue).HasMaxLength(ApplicationSetting.ValueMaxLength);

        // One platform row (TenantId NULL) and one row per tenant per code. HasFilter(null) stops EF from adding
        // "WHERE TenantId IS NOT NULL", which would allow duplicate platform rows.
        builder.HasIndex(s => new { s.Code, s.TenantId }).IsUnique().HasFilter(null);
    }
}
