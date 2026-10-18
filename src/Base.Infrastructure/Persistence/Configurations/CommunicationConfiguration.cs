using Base.Domain.Communications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class BroadcastConfiguration : IEntityTypeConfiguration<Broadcast>
{
    public void Configure(EntityTypeBuilder<Broadcast> builder)
    {
        builder.Property(b => b.Title).HasMaxLength(Broadcast.TitleMaxLength).IsRequired();
        builder.Property(b => b.AudienceKind).HasConversion<string>().HasMaxLength(20);
        builder.Property(b => b.AudienceCode).HasMaxLength(Broadcast.AudienceMaxLength).IsRequired();
        builder.Property(b => b.AudienceLabel).HasMaxLength(Broadcast.AudienceLabelMaxLength).IsRequired();
        builder.Property(b => b.AudienceJson).HasMaxLength(-1);
        builder.Property(b => b.Channels).HasMaxLength(Broadcast.ChannelsMaxLength).IsRequired();
        builder.Property(b => b.Subject).HasMaxLength(Broadcast.SubjectMaxLength);
        builder.Property(b => b.HtmlBody).HasMaxLength(-1);
        builder.Property(b => b.TextBody).HasMaxLength(-1);
        builder.Property(b => b.SmsBody).HasMaxLength(-1);
        builder.Property(b => b.WhatsAppTemplateName).HasMaxLength(Broadcast.WhatsAppNameMaxLength);
        builder.Property(b => b.WhatsAppLanguage).HasMaxLength(Broadcast.WhatsAppLanguageMaxLength);
        builder.Property(b => b.WhatsAppParameters).HasMaxLength(-1);
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(b => b.SenderName).HasMaxLength(Broadcast.NameMaxLength).IsRequired();
        builder.Property(b => b.Error).HasMaxLength(Broadcast.ErrorMaxLength);

        // The list (newest first) and the scheduler's "what is due" across tenants.
        builder.HasIndex(b => new { b.TenantId, b.ScheduledFor });
        builder.HasIndex(b => new { b.Status, b.ScheduledFor }).HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> builder)
    {
        builder.Property(t => t.Name).HasMaxLength(MessageTemplate.NameMaxLength).IsRequired();
        builder.Property(t => t.Subject).HasMaxLength(Broadcast.SubjectMaxLength);
        builder.Property(t => t.HtmlBody).HasMaxLength(-1);
        builder.Property(t => t.SmsBody).HasMaxLength(-1);
        builder.Property(t => t.WhatsAppTemplateName).HasMaxLength(Broadcast.WhatsAppNameMaxLength);
        builder.Property(t => t.WhatsAppLanguage).HasMaxLength(Broadcast.WhatsAppLanguageMaxLength);
        builder.Property(t => t.WhatsAppParameters).HasMaxLength(-1);
        builder.HasIndex(t => new { t.TenantId, t.Name }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}
