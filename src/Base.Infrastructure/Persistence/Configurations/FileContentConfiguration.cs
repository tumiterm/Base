using Base.Domain.Files;
using Base.Infrastructure.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class FileContentConfiguration : IEntityTypeConfiguration<FileContent>
{
    public void Configure(EntityTypeBuilder<FileContent> builder)
    {
        builder.ToTable("FileContents");
        builder.HasKey(c => c.ObjectKey);
        builder.Property(c => c.ObjectKey).HasMaxLength(StoredFile.ObjectKeyMaxLength);
        builder.Property(c => c.Content).HasMaxLength(-1).IsRequired();

        // The quota query: SUM(SizeBytes) per tenant without touching the content pages.
        builder.HasIndex(c => c.TenantId).IncludeProperties(c => c.SizeBytes);
    }
}
