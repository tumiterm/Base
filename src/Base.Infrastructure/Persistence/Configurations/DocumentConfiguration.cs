using Base.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Title).HasMaxLength(Document.TitleMaxLength).IsRequired();
        builder.Property(d => d.DocumentType).HasMaxLength(Document.TypeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(Document.DescriptionMaxLength);
        builder.Property(d => d.LinkedEntityType).HasMaxLength(Document.LinkTypeMaxLength);
        builder.Property(d => d.LinkedEntityId).HasMaxLength(Document.LinkIdMaxLength);
        builder.PrimitiveCollection(d => d.Tags).HasMaxLength(-1);

        builder.HasMany(d => d.Versions).WithOne().HasForeignKey(v => v.DocumentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => new { d.TenantId, d.LinkedEntityType, d.LinkedEntityId });
        builder.HasIndex(d => new { d.TenantId, d.DocumentType, d.Status });
        builder.HasIndex(d => new { d.TenantId, d.CreatedByUserId });
    }
}

internal sealed class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public void Configure(EntityTypeBuilder<DocumentVersion> builder)
    {
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.FileName).HasMaxLength(Domain.Files.StoredFile.FileNameMaxLength).IsRequired();
        builder.Property(v => v.ContentType).HasMaxLength(Domain.Files.StoredFile.ContentTypeMaxLength).IsRequired();
        builder.Property(v => v.Sha256).HasMaxLength(Domain.Files.StoredFile.Sha256Length).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(v => v.Comment).HasMaxLength(DocumentVersion.CommentMaxLength);
        builder.Property(v => v.TemplateCode).HasMaxLength(DocumentVersion.TemplateCodeMaxLength);

        // No foreign key to StoredFiles: purged files are hard-deleted while soft-deleted versions remain for the audit trail.
        builder.HasIndex(v => v.FileId);
        builder.HasIndex(v => new { v.DocumentId, v.Number }).IsUnique();
    }
}
