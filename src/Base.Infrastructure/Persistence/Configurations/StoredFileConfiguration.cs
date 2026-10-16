using Base.Domain.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        builder.Property(f => f.ProviderName).HasMaxLength(StoredFile.ProviderNameMaxLength).IsRequired();
        builder.Property(f => f.ObjectKey).HasMaxLength(StoredFile.ObjectKeyMaxLength).IsRequired();
        builder.Property(f => f.OriginalFileName).HasMaxLength(StoredFile.FileNameMaxLength).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(StoredFile.ContentTypeMaxLength).IsRequired();
        builder.Property(f => f.Category).HasMaxLength(StoredFile.CategoryMaxLength).IsRequired();
        builder.Property(f => f.Sha256).HasMaxLength(StoredFile.Sha256Length).IsFixedLength().IsUnicode(false).IsRequired();

        builder.HasIndex(f => new { f.ProviderName, f.ObjectKey }).IsUnique();
        builder.HasIndex(f => new { f.TenantId, f.OwnerUserId });
        builder.HasIndex(f => f.Sha256);
    }
}
