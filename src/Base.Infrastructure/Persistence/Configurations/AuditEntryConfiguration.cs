using Base.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntries");
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.EntityType).HasMaxLength(AuditEntry.EntityTypeMaxLength).IsRequired();
        builder.Property(e => e.EntityId).HasMaxLength(AuditEntry.EntityIdMaxLength).IsRequired();
        builder.Property(e => e.Action).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Changes).HasMaxLength(-1).IsRequired();
        builder.Property(e => e.UserName).HasMaxLength(AuditEntry.UserNameMaxLength).IsRequired();
        builder.Property(e => e.TraceId).HasMaxLength(AuditEntry.TraceIdMaxLength).IsUnicode(false);

        // The screens' queries: a tenant's latest entries, one record's history, one user's actions.
        builder.HasIndex(e => new { e.TenantId, e.OccurredAt });
        builder.HasIndex(e => new { e.TenantId, e.EntityType, e.EntityId, e.OccurredAt });
        builder.HasIndex(e => new { e.TenantId, e.UserId, e.OccurredAt });
        builder.HasIndex(e => e.OccurredAt); // retention
    }
}
