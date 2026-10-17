using Base.Infrastructure.Jobs;
using Base.Infrastructure.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class ScheduledJobStateConfiguration : IEntityTypeConfiguration<ScheduledJobState>
{
    public void Configure(EntityTypeBuilder<ScheduledJobState> builder)
    {
        builder.ToTable("ScheduledJobs");
        builder.HasKey(s => s.Code);
        builder.Property(s => s.Code).HasMaxLength(ScheduledJobState.CodeMaxLength).IsUnicode(false);
        builder.Property(s => s.LeaseOwner).HasMaxLength(200);
    }
}

internal sealed class ScheduledJobRunConfiguration : IEntityTypeConfiguration<ScheduledJobRun>
{
    public void Configure(EntityTypeBuilder<ScheduledJobRun> builder)
    {
        builder.ToTable("ScheduledJobRuns");
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Code).HasMaxLength(ScheduledJobState.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(r => r.Message).HasMaxLength(ScheduledJobRun.MessageMaxLength);
        builder.Property(r => r.Instance).HasMaxLength(200).IsRequired();
        builder.HasIndex(r => new { r.Code, r.StartedAt });
    }
}

internal sealed class ErrorLogEntryConfiguration : IEntityTypeConfiguration<ErrorLogEntry>
{
    public void Configure(EntityTypeBuilder<ErrorLogEntry> builder)
    {
        builder.ToTable("ErrorLog");
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Ticket).HasMaxLength(ErrorLogEntry.TicketMaxLength).IsUnicode(false).IsRequired();
        builder.Property(e => e.ExceptionType).HasMaxLength(ErrorLogEntry.TypeMaxLength).IsRequired();
        builder.Property(e => e.Message).HasMaxLength(ErrorLogEntry.MessageMaxLength).IsRequired();
        builder.Property(e => e.Method).HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(e => e.Path).HasMaxLength(ErrorLogEntry.PathMaxLength).IsRequired();
        builder.Property(e => e.User).HasMaxLength(256);
        builder.Property(e => e.TraceId).HasMaxLength(64).IsUnicode(false);
        builder.Property(e => e.Fingerprint).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(e => e.Details).HasMaxLength(ErrorLogEntry.DetailsMaxLength).IsRequired();
        builder.HasIndex(e => e.Ticket);
        builder.HasIndex(e => new { e.TenantId, e.OccurredAt });
    }
}
