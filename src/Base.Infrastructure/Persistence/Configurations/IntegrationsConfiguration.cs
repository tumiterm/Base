using Base.Domain.Integrations;
using Base.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> builder)
    {
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(ApiClient.NameMaxLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(ApiClient.DescriptionMaxLength);
        builder.Property(c => c.KeyPrefix).HasMaxLength(ApiClient.PrefixLength).IsUnicode(false).IsFixedLength().IsRequired();
        builder.Property(c => c.KeyHash).HasMaxLength(ApiClient.HashLength).IsUnicode(false).IsFixedLength().IsRequired();
        builder.PrimitiveCollection(c => c.Permissions).HasMaxLength(-1).HasField("_permissions");

        // Keys are found by prefix on every API call; a prefix is never reused, even across tenants.
        builder.HasIndex(c => c.KeyPrefix).IsUnique();
        builder.HasIndex(c => new { c.ExpiresAt, c.RevokedAt });
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Caller).HasMaxLength(IdempotencyRecord.CallerMaxLength).IsUnicode(false).IsRequired();
        builder.Property(r => r.Key).HasMaxLength(IdempotencyRecord.KeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(r => r.RequestHash).HasMaxLength(IdempotencyRecord.HashLength).IsUnicode(false).IsFixedLength().IsRequired();
        builder.Property(r => r.ContentType).HasMaxLength(IdempotencyRecord.ContentTypeMaxLength);
        builder.Property(r => r.Body).HasMaxLength(-1);
        builder.HasIndex(r => new { r.TenantId, r.Caller, r.Key }).IsUnique();
        builder.HasIndex(r => r.ExpiresAt);
    }
}
