using Base.Application.Common.Results;

namespace Base.Application.Operations;

/// <summary>
/// Recurring housekeeping (retention, purges...). Register implementations as <see cref="IScheduledJob"/> (scoped); the
/// scheduler runs each every <see cref="Interval"/> on one instance at a time, in system scope (all tenants), as
/// "System", and records every run. Jobs must be safe to run again (a crash mid-run means a retry later).
/// </summary>
public interface IScheduledJob
{
    /// <summary>Stable id, e.g. <c>audit-retention</c>.</summary>
    string Code { get; }

    string Name { get; }

    string Description { get; }

    TimeSpan Interval { get; }

    /// <summary>Does the work and returns a one-line summary for the run history (e.g. "Removed 120 entries").</summary>
    Task<string> RunAsync(CancellationToken cancellationToken);
}

public sealed record JobRunView(DateTimeOffset StartedAt, TimeSpan Duration, bool Succeeded, string? Message);

public sealed record JobStatusView(
    string Code,
    string Name,
    string Description,
    TimeSpan Interval,
    DateTimeOffset? NextRunAt,
    bool IsRunning,
    JobRunView? LastRun,
    IReadOnlyList<JobRunView> RecentRuns)
{
    /// <summary>Failed last time, or hasn't run for two intervals (scheduler stopped or the job hangs).</summary>
    public bool NeedsAttention(DateTimeOffset now) =>
        LastRun is { Succeeded: false } || (LastRun is { } last && last.StartedAt + (2 * Interval) < now);
}

public interface IJobScheduler
{
    Task<IReadOnlyList<JobStatusView>> StatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes the job due now; the scheduler starts it within seconds.</summary>
    Task<ValidationResponse> RunNowAsync(string code, CancellationToken cancellationToken = default);
}
