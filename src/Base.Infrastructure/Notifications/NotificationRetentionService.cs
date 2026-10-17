using Base.Application.Settings;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Base.Infrastructure.Notifications;

/// <summary>Deletes messages older than the <c>Notifications.RetentionDays</c> setting, once a day, in batches.</summary>
internal sealed partial class NotificationRetentionService(IServiceScopeFactory scopes, TimeProvider time, ILogger<NotificationRetentionService> logger) : BackgroundService
{
    private const int BatchSize = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
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
        var days = await scope.ServiceProvider.GetRequiredService<IApplicationSettingService>().GetIntAsync(SettingCodes.Notifications.RetentionDays, cancellationToken);
        var cutoff = time.GetUtcNow().AddDays(-days);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var total = 0;
        int deleted;
        do
        {
            // The log is not business data and is not audited, so bulk delete is fine here.
            deleted = await db.NotificationMessages.IgnoreQueryFilters()
                .Where(m => m.DateCreated < cutoff)
                .OrderBy(m => m.Id)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification retention removed {Count} messages")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification retention failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
