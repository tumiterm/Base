using Base.Application.Operations;
using Base.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Base.Infrastructure.Operations;

internal sealed class SystemStatus(AppDbContext db, HealthCheckService health, TimeProvider time) : ISystemStatus
{
    private const int LargestTables = 10;
    private const int DeadLettersShown = 20;

    public async Task<HealthReportView> HealthAsync(CancellationToken cancellationToken = default)
    {
        var report = await health.CheckHealthAsync(cancellationToken);
        return new HealthReportView(
            Map(report.Status),
            report.TotalDuration,
            report.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new HealthCheckView(
                e.Key,
                Map(e.Value.Status),
                e.Value.Description ?? e.Value.Exception?.Message,
                e.Value.Duration,
                e.Value.Data.ToDictionary(d => d.Key, d => Convert.ToString(d.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, StringComparer.Ordinal))).ToList());
    }

    public async Task<DatabaseStats> DatabaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var files = await db.Database.SqlQuery<FileSize>($"SELECT type_desc AS Type, CAST(SUM(size) * 8 / 1024.0 AS decimal(18,2)) AS Mb FROM sys.database_files GROUP BY type_desc")
                .ToListAsync(cancellationToken);
            var tables = await db.Database.SqlQuery<TableRow>($"""
                SELECT TOP ({LargestTables}) s.name + '.' + t.name AS [Table],
                       SUM(CASE WHEN p.index_id < 2 THEN p.row_count ELSE 0 END) AS [Rows],
                       CAST(SUM(p.reserved_page_count) * 8 / 1024.0 AS decimal(18,2)) AS ReservedMb
                FROM sys.dm_db_partition_stats p
                JOIN sys.tables t ON p.object_id = t.object_id
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                GROUP BY s.name, t.name
                ORDER BY SUM(p.reserved_page_count) DESC
                """).ToListAsync(cancellationToken);
            return new DatabaseStats(
                files.Where(f => string.Equals(f.Type, "ROWS", StringComparison.Ordinal)).Sum(f => f.Mb),
                files.Where(f => string.Equals(f.Type, "LOG", StringComparison.Ordinal)).Sum(f => f.Mb),
                tables.Select(t => new TableSize(t.Table, t.Rows, t.ReservedMb)).ToList());
        }
        catch (SqlException)
        {
            // The login lacks VIEW DATABASE STATE (common on managed databases): show what we can.
            return new DatabaseStats(null, null, []);
        }
    }

    public async Task<IReadOnlyList<StorageUsage>> StorageAsync(CancellationToken cancellationToken = default)
    {
        var usage = await db.StoredFiles.IgnoreQueryFilters()
            .GroupBy(f => new { f.TenantId, f.ProviderName })
            .Select(g => new
            {
                g.Key.TenantId,
                g.Key.ProviderName,
                Files = g.Count(f => !f.IsDeleted),
                Bytes = g.Sum(f => f.SizeBytes),
                Deleted = g.Count(f => f.IsDeleted),
            })
            .ToListAsync(cancellationToken);
        var tenants = await db.Tenants.IgnoreQueryFilters().ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        return usage
            .Select(u => new StorageUsage(tenants.GetValueOrDefault(u.TenantId) ?? u.TenantId.ToString(), u.ProviderName, u.Files, u.Bytes, u.Deleted))
            .OrderByDescending(u => u.Bytes)
            .ToList();
    }

    public async Task<OutboxStats> OutboxAsync(CancellationToken cancellationToken = default)
    {
        var pending = db.OutboxMessages.Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null);
        var count = await pending.CountAsync(cancellationToken);
        var oldest = count == 0 ? null : await pending.MinAsync(m => (DateTimeOffset?)m.OccurredAt, cancellationToken);
        var deadLettered = db.OutboxMessages.Where(m => m.DeadLetteredAt != null);
        var dead = await deadLettered.CountAsync(cancellationToken);
        var shown = await deadLettered.OrderByDescending(m => m.DeadLetteredAt).Take(DeadLettersShown)
            .Select(m => new OutboxMessageView(m.Id, m.Type, m.OccurredAt, m.Attempts, m.LastError, m.DeadLetteredAt))
            .ToListAsync(cancellationToken);
        return new OutboxStats(count, oldest is { } o ? time.GetUtcNow() - o : null, dead, shown.Select(m => m with { Type = ShortType(m.Type) }).ToList());
    }

    public async Task<bool> RetryOutboxMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await db.OutboxMessages.SingleOrDefaultAsync(m => m.Id == messageId && m.DeadLetteredAt != null, cancellationToken);
        if (message is null)
        {
            return false;
        }

        message.DeadLetteredAt = null;
        message.Attempts = 0;
        message.LockedUntil = null;
        message.NextAttemptAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string ShortType(string type) => type.Split(',')[0].Split('.')[^1];

    private static HealthState Map(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => HealthState.Healthy,
        HealthStatus.Degraded => HealthState.Degraded,
        _ => HealthState.Unhealthy,
    };

    private sealed class FileSize
    {
        public string Type { get; set; } = string.Empty;

        public decimal Mb { get; set; }
    }

    private sealed class TableRow
    {
        public string Table { get; set; } = string.Empty;

        public long Rows { get; set; }

        public decimal ReservedMb { get; set; }
    }
}
