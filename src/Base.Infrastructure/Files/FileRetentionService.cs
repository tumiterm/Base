using Base.Application.Common.Abstractions;
using Base.Application.Files;
using Base.Application.Settings;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Base.Infrastructure.Files;

/// <summary>
/// Once a day, removes the bytes and metadata of files deleted (or expired) more than <c>Files.DeletedRetentionDays</c>
/// ago. Until then a delete can be undone. Rows are removed through the change tracker, so the purge is audited.
/// </summary>
internal sealed partial class FileRetentionService(IServiceScopeFactory scopes, TimeProvider time, ILogger<FileRetentionService> logger) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), time, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
        do
        {
            try
            {
                var purged = await PurgeAsync(stoppingToken);
                if (purged > 0)
                {
                    LogPurged(logger, purged);
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
        using var system = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginSystemScope();
        var days = await scope.ServiceProvider.GetRequiredService<IApplicationSettingService>().GetIntAsync(SettingCodes.Files.DeletedRetentionDays, cancellationToken);
        var cutoff = time.GetUtcNow().AddDays(-days);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var providers = scope.ServiceProvider.GetRequiredService<IFileStorageProviderResolver>();

        var total = 0;
        var skipped = new HashSet<Guid>();
        while (true)
        {
            var batch = await db.StoredFiles.IgnoreQueryFilters()
                .Where(f => (f.IsDeleted && f.DateDeleted < cutoff) || (f.ExpiresAt != null && f.ExpiresAt < cutoff))
                .Where(f => !skipped.Contains(f.Id))
                .OrderBy(f => f.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return total;
            }

            foreach (var file in batch)
            {
                if (!providers.TryGet(file.ProviderName, out var provider))
                {
                    LogProviderMissing(logger, file.Id, file.ProviderName);
                    skipped.Add(file.Id);
                    continue;
                }

                try
                {
                    await provider.DeleteAsync(file.ObjectKey, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Keep the row so tomorrow's run tries again; never lose track of bytes still in storage.
                    LogDeleteFailed(logger, exception, file.Id, file.ProviderName);
                    skipped.Add(file.Id);
                    continue;
                }

                db.StoredFiles.Remove(file);
                total++;
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "File retention purged {Count} files")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "File retention failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot purge file {FileId}: provider {Provider} is not configured")]
    private static partial void LogProviderMissing(ILogger logger, Guid fileId, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot purge file {FileId} from {Provider}; will retry tomorrow")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, Guid fileId, string provider);
}
