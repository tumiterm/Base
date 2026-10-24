using Base.Application.Common.Abstractions;
using Base.Application.Files;
using Base.Application.Operations;
using Base.Application.Settings;
using Base.Infrastructure.Outbox;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Jobs;

/// <summary>Deletes audit entries older than the <c>Audit.RetentionDays</c> setting, in batches, across tenants.</summary>
internal sealed class AuditRetentionJob(AppDbContext db, IApplicationSettingService settings, TimeProvider time) : IScheduledJob
{
    private const int BatchSize = 5000;

    /// <summary>Financial records: their history is kept whatever the retention (tax law asks for years, not months).</summary>
    private static readonly string[] KeptForGood =
        [nameof(Domain.Payments.Payment), nameof(Domain.Payments.RecurringPayment), nameof(Domain.Subscriptions.TenantSubscription), nameof(Domain.Subscriptions.Plan), nameof(Domain.Subscriptions.PlanEntitlement)];

    public string Code => "audit-retention";

    public string Name => "Audit retention";

    public string Description => "Deletes audit entries older than Audit.RetentionDays, except those of payments, subscriptions and plans.";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken) => $"Removed {await PurgeAsync(cancellationToken)} entries.";

    internal async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-await settings.GetIntAsync(SettingCodes.Audit.RetentionDays, cancellationToken));
        var total = 0;
        int deleted;
        do
        {
            // All tenants: retention is a platform policy. ExecuteDelete bypasses the append-only guard by design.
            deleted = await db.AuditEntries.IgnoreQueryFilters()
                .Where(e => e.OccurredAt < cutoff && !KeptForGood.Contains(e.EntityType))
                .OrderBy(e => e.Id)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        return total;
    }
}

/// <summary>Deletes notification messages older than the <c>Notifications.RetentionDays</c> setting, including in-app ones.</summary>
internal sealed class NotificationRetentionJob(AppDbContext db, IApplicationSettingService settings, TimeProvider time) : IScheduledJob
{
    private const int BatchSize = 5000;

    public string Code => "notification-retention";

    public string Name => "Notification log retention";

    public string Description => "Deletes sent, failed and in-app messages older than Notifications.RetentionDays.";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken) => $"Removed {await PurgeAsync(cancellationToken)} messages.";

    internal async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-await settings.GetIntAsync(SettingCodes.Notifications.RetentionDays, cancellationToken));
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
}

/// <summary>
/// Removes the bytes and metadata of files deleted (or expired) more than <c>Files.DeletedRetentionDays</c> ago. Until
/// then a delete can be undone. Rows are removed through the change tracker, so the purge is audited; the object goes
/// first, so bytes are never orphaned.
/// </summary>
internal sealed partial class FileRetentionJob(
    AppDbContext db, IFileStorageProviderResolver providers, IApplicationSettingService settings, ITenantContext tenant, TimeProvider time, ILogger<FileRetentionJob> logger) : IScheduledJob
{
    private const int BatchSize = 200;

    public string Code => "file-retention";

    public string Name => "File purge";

    public string Description => "Removes the content of files deleted or expired longer than Files.DeletedRetentionDays ago.";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken) => $"Purged {await PurgeAsync(cancellationToken)} files.";

    internal async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        using var system = tenant.BeginSystemScope();
        var cutoff = time.GetUtcNow().AddDays(-await settings.GetIntAsync(SettingCodes.Files.DeletedRetentionDays, cancellationToken));
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
                    // Keep the row so the next run tries again; never lose track of bytes still in storage.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot purge file {FileId}: provider {Provider} is not configured")]
    private static partial void LogProviderMissing(ILogger logger, Guid fileId, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot purge file {FileId} from {Provider}; will retry next run")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, Guid fileId, string provider);
}

/// <summary>Deletes processed outbox messages older than <c>Outbox:RetentionDays</c> (dead letters stay until handled).</summary>
internal sealed class OutboxCleanupJob(AppDbContext db, IOptions<OutboxOptions> options, TimeProvider time) : IScheduledJob
{
    public string Code => "outbox-cleanup";

    public string Name => "Outbox cleanup";

    public string Description => "Deletes processed outbox messages older than Outbox:RetentionDays.";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-options.Value.RetentionDays);
        var deleted = await db.OutboxMessages.Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        return $"Removed {deleted} processed messages.";
    }
}

/// <summary>Deletes recorded errors older than the <c>System.ErrorRetentionDays</c> setting.</summary>
internal sealed class ErrorLogRetentionJob(AppDbContext db, IApplicationSettingService settings, TimeProvider time) : IScheduledJob
{
    public string Code => "error-log-retention";

    public string Name => "Error log retention";

    public string Description => "Deletes recorded errors older than System.ErrorRetentionDays.";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-await settings.GetIntAsync(SettingCodes.System.ErrorRetentionDays, cancellationToken));
        var deleted = await db.ErrorLogEntries.Where(e => e.OccurredAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        return $"Removed {deleted} errors.";
    }
}
