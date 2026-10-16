using System.ComponentModel.DataAnnotations;

namespace Base.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public bool Enabled { get; set; } = true;

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 5;

    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    [Range(1, 50)]
    public int MaxAttempts { get; set; } = 10;

    [Range(1, 3600)]
    public int LockSeconds { get; set; } = 120;

    /// <summary>Processed messages older than this are deleted.</summary>
    [Range(1, 365)]
    public int RetentionDays { get; set; } = 7;
}
