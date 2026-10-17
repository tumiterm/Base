using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Operations;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Jobs;

public sealed class JobSchedulerOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Off for a host that should never run housekeeping (e.g. a read-only replica).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Let the app finish starting before the first jobs run.</summary>
    [Range(0, 3600)]
    public int StartDelaySeconds { get; set; } = 60;

    [Range(1, 3600)]
    public int PollSeconds { get; set; } = 30;

    [Range(1, 365)]
    public int HistoryDays { get; set; } = 30;
}

/// <summary>When each job runs next, and who holds its lease. One row per job; plumbing like the outbox (not audited).</summary>
public sealed class ScheduledJobState
{
    public const int CodeMaxLength = 100;

    public string Code { get; set; } = string.Empty;

    public DateTimeOffset NextRunAt { get; set; }

    /// <summary>While set and in the future, an instance is running the job; others leave it alone.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    public string? LeaseOwner { get; set; }
}

public sealed class ScheduledJobRun
{
    public const int MessageMaxLength = 2000;

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public long DurationMs { get; set; }

    public bool Succeeded { get; set; }

    public string? Message { get; set; }

    public string Instance { get; set; } = string.Empty;
}

/// <summary>Wakes the scheduler early (after "run now").</summary>
internal sealed class JobSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Dispose() => _signal.Dispose();

    public void Wake()
    {
        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Runs every <see cref="IScheduledJob"/> on its interval. Safe across instances: a job runs only on the instance that
/// wins its lease (a conditional UPDATE). Each run gets its own DI scope, system scope (all tenants) and "System" user,
/// and is recorded with its duration and outcome.
/// </summary>
internal sealed partial class JobSchedulerService(
    IServiceScopeFactory scopes,
    JobSignal signal,
    IOptions<JobSchedulerOptions> options,
    TimeProvider time,
    ILogger<JobSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan MinimumLease = TimeSpan.FromHours(1);
    private readonly string _instance = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        await Task.Delay(TimeSpan.FromSeconds(settings.StartDelaySeconds), time, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogLoopFailed(logger, exception);
            }

            await signal.WaitAsync(TimeSpan.FromSeconds(settings.PollSeconds), stoppingToken);
        }
    }

    /// <summary>Runs every job that is due and not leased elsewhere. Returns how many ran.</summary>
    internal async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobs = scope.ServiceProvider.GetServices<IScheduledJob>().ToList();
            await EnsureStatesAsync(db, jobs, cancellationToken);
            var now = time.GetUtcNow();
            due = await db.ScheduledJobStates.AsNoTracking()
                .Where(s => s.NextRunAt <= now && (s.LeaseUntil == null || s.LeaseUntil < now))
                .Select(s => s.Code)
                .ToListAsync(cancellationToken);
            due = [.. due.Where(code => jobs.Exists(j => string.Equals(j.Code, code, StringComparison.Ordinal)))];
        }

        var ran = 0;
        foreach (var code in due)
        {
            if (await RunAsync(code, cancellationToken))
            {
                ran++;
            }
        }

        return ran;
    }

    /// <summary>Claims and runs one job. False when another instance holds it or it isn't due.</summary>
    internal async Task<bool> RunAsync(string code, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var job = scope.ServiceProvider.GetServices<IScheduledJob>().Single(j => string.Equals(j.Code, code, StringComparison.Ordinal));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lease = started + (job.Interval > MinimumLease ? job.Interval : MinimumLease);

        // Infrastructure plumbing, not business data: a conditional bulk update is the lease.
        var claimed = await db.ScheduledJobStates
            .Where(s => s.Code == code && s.NextRunAt <= started && (s.LeaseUntil == null || s.LeaseUntil < started))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LeaseUntil, lease).SetProperty(s => s.LeaseOwner, _instance), cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        bool succeeded;
        string message;
        try
        {
            using var system = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginSystemScope();
            message = await job.RunAsync(cancellationToken);
            succeeded = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogJobFailed(logger, exception, code);
            message = $"{exception.GetType().Name}: {exception.Message}";
            succeeded = false;
        }

        await RecordAsync(code, job.Interval, started, stopwatch.Elapsed, succeeded, message, cancellationToken);
        return true;
    }

    private async Task RecordAsync(string code, TimeSpan interval, DateTimeOffset started, TimeSpan duration, bool succeeded, string message, CancellationToken cancellationToken)
    {
        // A fresh scope: the job's context may hold failed changes.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.ScheduledJobStates.SingleAsync(s => s.Code == code, cancellationToken);
        state.NextRunAt = started + interval;
        state.LeaseUntil = null;
        state.LeaseOwner = null;
        db.ScheduledJobRuns.Add(new ScheduledJobRun
        {
            Id = Domain.Common.SequentialGuid.Next(),
            Code = code,
            StartedAt = started,
            DurationMs = (long)duration.TotalMilliseconds,
            Succeeded = succeeded,
            Message = message.Length > ScheduledJobRun.MessageMaxLength ? message[..ScheduledJobRun.MessageMaxLength] : message,
            Instance = _instance,
        });
        await db.SaveChangesAsync(cancellationToken);

        var cutoff = time.GetUtcNow().AddDays(-options.Value.HistoryDays);
        await db.ScheduledJobRuns.Where(r => r.Code == code && r.StartedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task EnsureStatesAsync(AppDbContext db, List<IScheduledJob> jobs, CancellationToken cancellationToken)
    {
        var known = await db.ScheduledJobStates.Select(s => s.Code).ToListAsync(cancellationToken);
        var missing = jobs.Where(j => !known.Contains(j.Code, StringComparer.Ordinal)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        db.ScheduledJobStates.AddRange(missing.Select(j => new ScheduledJobState { Code = j.Code, NextRunAt = time.GetUtcNow() }));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another instance inserted them first: same rows, nothing to do.
            db.ChangeTracker.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled job {Job} failed")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, string job);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job scheduler loop failed")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);
}

/// <summary>Job status for the system page, and "run now".</summary>
internal sealed class JobSchedulerStatus(AppDbContext db, IEnumerable<IScheduledJob> jobs, JobSignal signal, TimeProvider time) : IJobScheduler
{
    private const int RecentRuns = 10;

    public async Task<IReadOnlyList<JobStatusView>> StatusAsync(CancellationToken cancellationToken = default)
    {
        var states = await db.ScheduledJobStates.AsNoTracking().ToDictionaryAsync(s => s.Code, StringComparer.Ordinal, cancellationToken);
        var codes = jobs.Select(j => j.Code).ToList();
        var runs = (await db.ScheduledJobRuns.AsNoTracking()
                .Where(r => codes.Contains(r.Code))
                .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id) // ids are sequential: ties keep run order
                .Take(RecentRuns * Math.Max(1, codes.Count) * 3)
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Take(RecentRuns).Select(r => new JobRunView(r.StartedAt, TimeSpan.FromMilliseconds(r.DurationMs), r.Succeeded, r.Message)).ToList(), StringComparer.Ordinal);
        var now = time.GetUtcNow();

        return jobs.OrderBy(j => j.Name, StringComparer.Ordinal).Select(j =>
        {
            var state = states.GetValueOrDefault(j.Code);
            var recent = runs.GetValueOrDefault(j.Code) ?? [];
            return new JobStatusView(j.Code, j.Name, j.Description, j.Interval, state?.NextRunAt, state?.LeaseUntil > now, recent.FirstOrDefault(), recent);
        }).ToList();
    }

    public async Task<ValidationResponse> RunNowAsync(string code, CancellationToken cancellationToken = default)
    {
        if (!jobs.Any(j => string.Equals(j.Code, code, StringComparison.Ordinal)))
        {
            return ValidationResponse.NotFound("Unknown job.");
        }

        var now = time.GetUtcNow();
        var updated = await db.ScheduledJobStates.Where(s => s.Code == code).ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRunAt, now), cancellationToken);
        if (updated == 0)
        {
            db.ScheduledJobStates.Add(new ScheduledJobState { Code = code, NextRunAt = now });
            await db.SaveChangesAsync(cancellationToken);
        }

        signal.Wake();
        return ValidationResponse.Success("The job will start within a few seconds.");
    }
}
