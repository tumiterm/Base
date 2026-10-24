using Base.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class NotificationMessageConfiguration : IEntityTypeConfiguration<NotificationMessage>
{
    public void Configure(EntityTypeBuilder<NotificationMessage> builder)
    {
        builder.ToTable("NotificationMessages");
        builder.Property(m => m.TemplateCode).HasMaxLength(NotificationMessage.TemplateCodeMaxLength).IsRequired();
        builder.Property(m => m.Category).HasMaxLength(NotificationMessage.CategoryMaxLength).IsRequired();
        builder.Property(m => m.Channel).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.RecipientAddress).HasMaxLength(NotificationMessage.AddressMaxLength).IsRequired();
        builder.Property(m => m.RecipientName).HasMaxLength(NotificationMessage.NameMaxLength);
        builder.Property(m => m.Subject).HasMaxLength(NotificationMessage.SubjectMaxLength);
        builder.Property(m => m.Body).HasMaxLength(-1);
        builder.Property(m => m.HtmlBody).HasMaxLength(-1);
        builder.Property(m => m.TemplateParameters).HasMaxLength(-1);
        builder.Property(m => m.DataJson).HasMaxLength(-1);
        builder.Property(m => m.RecipientJson).HasMaxLength(-1);
        builder.Property(m => m.FallbackChannels).HasMaxLength(100);
        builder.Property(m => m.Provider).HasMaxLength(NotificationMessage.ProviderMaxLength);
        builder.Property(m => m.ProviderMessageId).HasMaxLength(NotificationMessage.ProviderMessageIdMaxLength);
        builder.Property(m => m.LastError).HasMaxLength(NotificationMessage.ErrorMaxLength);
        builder.Property(m => m.WhatsAppTemplate).HasMaxLength(NotificationMessage.WhatsAppTemplateMaxLength);
        builder.Ignore(m => m.Content);

        // The inbox (a user's unread in-app messages), the admin log, and retention.
        builder.HasIndex(m => new { m.TenantId, m.RecipientUserId, m.Channel, m.ReadAt });
        builder.HasIndex(m => new { m.TenantId, m.DateCreated });
        builder.HasIndex(m => m.DateCreated);

        // A broadcast's progress.
        builder.HasIndex(m => new { m.TenantId, m.SourceId, m.Status }).HasFilter("[SourceId] IS NOT NULL");
    }
}

internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("NotificationPreferences");
        builder.Property(p => p.Category).HasMaxLength(NotificationMessage.CategoryMaxLength).IsRequired();
        builder.Property(p => p.Channel).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(p => new { p.TenantId, p.UserId, p.Category, p.Channel }).IsUnique();
    }
}

internal sealed class NotificationTemplateOverrideConfiguration : IEntityTypeConfiguration<NotificationTemplateOverride>
{
    public void Configure(EntityTypeBuilder<NotificationTemplateOverride> builder)
    {
        builder.ToTable("NotificationTemplateOverrides");
        builder.Property(o => o.TemplateCode).HasMaxLength(NotificationMessage.TemplateCodeMaxLength).IsRequired();
        builder.Property(o => o.Part).HasMaxLength(NotificationTemplateOverride.PartMaxLength).IsRequired();
        builder.Property(o => o.Content).HasMaxLength(-1).IsRequired();
        builder.HasIndex(o => new { o.TenantId, o.TemplateCode, o.Part }).IsUnique();
    }
}

internal sealed class NotificationTemplateRevisionConfiguration : IEntityTypeConfiguration<NotificationTemplateRevision>
{
    public void Configure(EntityTypeBuilder<NotificationTemplateRevision> builder)
    {
        builder.ToTable("NotificationTemplateRevisions");
        builder.Property(r => r.TemplateCode).HasMaxLength(NotificationMessage.TemplateCodeMaxLength).IsRequired();
        builder.Property(r => r.PartsJson).HasMaxLength(-1).IsRequired();
        builder.Property(r => r.Note).HasMaxLength(NotificationTemplateRevision.NoteMaxLength);
        builder.HasIndex(r => new { r.TenantId, r.TemplateCode, r.Number }).IsUnique();
    }
}
