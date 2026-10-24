using System.Diagnostics.Metrics;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Operations;
using Base.Application.Settings;
using Base.Infrastructure.Files;
using Base.Infrastructure.Jobs;
using Base.Infrastructure.Outbox;
using Base.Infrastructure.Persistence;
using Base.Infrastructure.Persistence.Interceptors;
using Base.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Operations;

public sealed class OperationsTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly FlakyJob _flaky = new();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(
            sql.NewDatabase(),
            services =>
            {
                services.AddSingleton<ICurrentUser>(new TestCurrentUser().WithAllPermissions());
                services.AddSingleton(_flaky);
                services.AddScoped<IScheduledJob>(sp => sp.GetRequiredService<FlakyJob>());
            },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Database:SlowQueryThresholdMs"] = "150" });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Due_jobs_run_once_are_recorded_and_wait_for_their_interval()
    {
        var scheduler = Scheduler();

        (await scheduler.RunDueAsync(CancellationToken.None)).ShouldBe(13, "every job is due on first start");
        (await scheduler.RunDueAsync(CancellationToken.None)).ShouldBe(0, "nothing is due again until its interval passes");

        var status = await Jobs(j => j.StatusAsync());
        var audit = status.Single(j => j.Code == "audit-retention");
        audit.LastRun!.Succeeded.ShouldBeTrue();
        audit.LastRun.Message.ShouldBe("Removed 0 entries.");
        audit.NextRunAt.ShouldBe(_app.Time.GetUtcNow().AddDays(1));

        _app.Time.Advance(TimeSpan.FromHours(1));
        (await scheduler.RunDueAsync(CancellationToken.None)).ShouldBe(6, "the hourly outbox cleanup, subscription lifecycle, idempotency cleanup, scheduled messages (every minute), payment expiry (every 15 minutes) and the flaky test job");
    }

    [Fact]
    public async Task Failures_are_recorded_and_flag_the_job_and_run_now_makes_it_due()
    {
        _flaky.FailNext = true;
        var scheduler = Scheduler();
        await scheduler.RunDueAsync(CancellationToken.None);

        var flaky = (await Jobs(j => j.StatusAsync())).Single(j => j.Code == FlakyJob.JobCode);
        flaky.LastRun!.Succeeded.ShouldBeFalse();
        flaky.LastRun.Message.ShouldBe("InvalidOperationException: boom");
        flaky.NeedsAttention(_app.Time.GetUtcNow()).ShouldBeTrue();
        var health = await Status(s => s.HealthAsync());
        var check = health.Checks.Single(c => c.Name == "scheduled-jobs");
        check.State.ShouldBe(HealthState.Degraded);
        check.Description.ShouldBe("Failed or overdue: Flaky test job.");

        (await Jobs(j => j.RunNowAsync(FlakyJob.JobCode))).IsSuccess.ShouldBeTrue();
        (await scheduler.RunDueAsync(CancellationToken.None)).ShouldBe(1);
        (await Jobs(j => j.StatusAsync())).Single(j => j.Code == FlakyJob.JobCode).RecentRuns.Select(r => r.Succeeded).ShouldBe([true, false]);
        (await Jobs(j => j.RunNowAsync("nonsense"))).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Only_one_instance_wins_a_jobs_lease()
    {
        var first = Scheduler();
        var second = Scheduler();
        await first.RunDueAsync(CancellationToken.None);
        await Jobs(j => j.RunNowAsync(FlakyJob.JobCode));
        _flaky.Gate = new TaskCompletionSource();

        var running = first.RunAsync(FlakyJob.JobCode, CancellationToken.None);
        await _flaky.Started.Task;
        (await second.RunAsync(FlakyJob.JobCode, CancellationToken.None)).ShouldBeFalse("the first instance holds the lease");
        (await Jobs(j => j.StatusAsync())).Single(j => j.Code == FlakyJob.JobCode).IsRunning.ShouldBeTrue();

        _flaky.Gate.SetResult();
        (await running).ShouldBeTrue();
        _flaky.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task Dead_lettered_messages_degrade_the_outbox_until_retried()
    {
        var id = await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var message = new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "Base.Domain.Something, Base.Domain",
                Payload = "{}",
                OccurredAt = _app.Time.GetUtcNow(),
                NextAttemptAt = _app.Time.GetUtcNow(),
                Attempts = 10,
                DeadLetteredAt = _app.Time.GetUtcNow(),
                LastError = "handler blew up",
            };
            db.OutboxMessages.Add(message);
            await db.SaveChangesAsync();
            return message.Id;
        });

        var outbox = await Status(s => s.OutboxAsync());
        outbox.DeadLettered.ShouldBe(1);
        outbox.DeadLetters.ShouldHaveSingleItem().Type.ShouldBe("Something");
        (await Status(s => s.HealthAsync())).Checks.Single(c => c.Name == "outbox").State.ShouldBe(HealthState.Degraded);

        (await Status(s => s.RetryOutboxMessageAsync(id))).ShouldBeTrue();
        var retried = await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().OutboxMessages.SingleAsync(m => m.Id == id));
        (retried.DeadLetteredAt, retried.Attempts).ShouldBe((null, 0));
        (await Status(s => s.RetryOutboxMessageAsync(id))).ShouldBeFalse("it is no longer dead-lettered");
    }

    [Fact]
    public async Task Database_file_quota_is_degraded_from_80_percent()
    {
        await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IApplicationSettingService>().SetAsync(SettingCodes.Files.DatabaseTenantQuotaMb, "1"));
        await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.FileContents.Add(new FileContent { ObjectKey = "k", TenantId = _app.DefaultTenantId, SizeBytes = 900 * 1024, Content = [1], CreatedAt = _app.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        });

        var check = (await Status(s => s.HealthAsync())).Checks.Single(c => c.Name == "database-file-quota");

        check.State.ShouldBe(HealthState.Degraded);
        check.Description.ShouldBe("Fullest tenant at 88% of its quota.");
    }

    [Fact]
    public async Task The_report_covers_every_dependency_and_the_database_stats_are_read()
    {
        var health = await Status(s => s.HealthAsync());
        health.Checks.Select(c => c.Name).ShouldBe(["database", "database-file-quota", "file-storage", "notification-providers", "outbox", "scheduled-jobs"]);
        health.Checks.Single(c => c.Name == "file-storage").Data.ShouldContainKey("LocalDisk");

        var database = await Status(s => s.DatabaseAsync());
        database.DataMb.ShouldNotBeNull();
        database.LargestTables.ShouldContain(t => t.Table == "dbo.ApplicationSettings");
    }

    [Fact]
    public async Task Errors_are_found_by_ticket_and_each_tenant_sees_its_own()
    {
        var other = Guid.NewGuid();
        await _app.InScopeAsync(null, async sp =>
        {
            var log = sp.GetRequiredService<IErrorLog>();
            await log.RecordAsync(Error("E-MINE00001", _app.DefaultTenantId));
            await log.RecordAsync(Error("E-OTHER0001", other));
            await log.RecordAsync(Error("E-NOTENANT1", null));
        });

        var mine = await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IErrorLog>().SearchAsync(null, new PageRequest()));
        mine.Items.Select(e => e.Ticket).ShouldBe(["E-MINE00001", "E-NOTENANT1"], ignoreOrder: true);
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IErrorLog>().GetAsync("E-OTHER0001"))).ShouldBeNull();
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IErrorLog>().GetAsync("E-MINE00001")))!.Details.ShouldContain("at Somewhere");
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IErrorLog>().SearchAsync("Timeout", new PageRequest()))).Total.ShouldBe(2);
    }

    [Fact]
    public async Task Slow_commands_are_counted()
    {
        // The meter is process-wide and other tests run alongside: count only what this test's own flow records.
        var slow = 0L;
        var ours = new AsyncLocal<bool> { Value = true };
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SlowQueryInterceptor.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            if (ours.Value)
            {
                Interlocked.Add(ref slow, value);
            }
        });
        listener.Start();

        await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync($"WAITFOR DELAY '00:00:00.300'"));
        await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync($"SELECT 1"));

        Interlocked.Read(ref slow).ShouldBe(1);
    }

    private ErrorRecord Error(string ticket, Guid? tenant) =>
        new(ticket, _app.Time.GetUtcNow(), "System.TimeoutException", "The operation timed out", "GET", "/x", "Ann", tenant, "trace", "f00d", "System.TimeoutException\n   at Somewhere");

    private JobSchedulerService Scheduler() => ActivatorUtilities.CreateInstance<JobSchedulerService>(_app.Services);

    private Task<T> Jobs<T>(Func<IJobScheduler, Task<T>> work) => _app.InScopeAsync(null, sp => work(sp.GetRequiredService<IJobScheduler>()));

    private Task<T> Status<T>(Func<ISystemStatus, Task<T>> work) => _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<ISystemStatus>()));

    /// <summary>A job a test can make fail, or hold mid-run.</summary>
    internal sealed class FlakyJob : IScheduledJob
    {
        public const string JobCode = "flaky";

        private int _runs;

        public bool FailNext { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Started { get; } = new();

        public int Runs => _runs;

        public string Code => JobCode;

        public string Name => "Flaky test job";

        public string Description => "For tests.";

        public TimeSpan Interval => TimeSpan.FromHours(1);

        public async Task<string> RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            Started.TrySetResult();
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("boom");
            }

            return "ok";
        }
    }
}
