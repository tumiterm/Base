using Base.Application.Settings;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Base.Infrastructure.Auditing;

/// <summary>Deletes audit entries older than the <c>Audit.RetentionDays</c> setting, once a day, in small batches.</summary>
internal sealed partial class AuditRetentionService(IServiceScopeFactory scopes, TimeProvider time, ILogger<AuditRetentionService> logger) : BackgroundService
{
    private const int BatchSize = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before doing housekeeping.
        await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
        do
        {
            try
            {
                var deleted = await PurgeAsync(stoppingToken);
                if (deleted > 0)
                {
                    LogPurged(logger, deleted);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var days = await scope.ServiceProvider.GetRequiredService<IApplicationSettingService>().GetIntAsync(SettingCodes.Audit.RetentionDays, cancellationToken);
        var cutoff = time.GetUtcNow().AddDays(-days);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var total = 0;
        int deleted;
        do
        {
            // All tenants: retention is a platform policy. ExecuteDelete bypasses the append-only guard by design.
            deleted = await db.AuditEntries.IgnoreQueryFilters()
                .Where(e => e.OccurredAt < cutoff)
                .OrderBy(e => e.Id)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit retention removed {Count} entries")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit retention failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
