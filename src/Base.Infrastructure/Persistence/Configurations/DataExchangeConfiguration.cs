using Base.Domain.DataExchange;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class ExportJobConfiguration : IEntityTypeConfiguration<ExportJob>
{
    public void Configure(EntityTypeBuilder<ExportJob> builder)
    {
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.ExporterCode).HasMaxLength(ExportJob.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(j => j.FilterJson).HasMaxLength(ExportJob.FilterMaxLength).IsRequired();
        builder.Property(j => j.Error).HasMaxLength(ExportJob.ErrorMaxLength);
        builder.HasIndex(j => new { j.TenantId, j.RequestedByUserId, j.DateCreated });
    }
}

internal sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> builder)
    {
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.ImporterCode).HasMaxLength(ImportJob.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(j => j.FileName).HasMaxLength(Domain.Files.StoredFile.FileNameMaxLength).IsRequired();
        builder.Property(j => j.ErrorsJson).HasMaxLength(-1).IsRequired();
        builder.Property(j => j.Error).HasMaxLength(ImportJob.ErrorMaxLength);
        builder.HasIndex(j => new { j.TenantId, j.RequestedByUserId, j.DateCreated });
    }
}
