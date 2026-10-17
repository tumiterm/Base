namespace Base.Application.Operations;

public enum HealthState
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2,
}

/// <param name="Data">Facts behind the result (counts, ages, per-provider results), safe to show administrators.</param>
public sealed record HealthCheckView(string Name, HealthState State, string? Description, TimeSpan Duration, IReadOnlyDictionary<string, string> Data);

public sealed record HealthReportView(HealthState State, TimeSpan Duration, IReadOnlyList<HealthCheckView> Checks);

public sealed record TableSize(string Table, long Rows, decimal ReservedMb);

/// <summary>Null parts mean the database login may not read them (VIEW DATABASE STATE).</summary>
public sealed record DatabaseStats(decimal? DataMb, decimal? LogMb, IReadOnlyList<TableSize> LargestTables);

public sealed record StorageUsage(string Tenant, string Provider, int Files, long Bytes, int DeletedFiles);

public sealed record OutboxMessageView(Guid Id, string Type, DateTimeOffset OccurredAt, int Attempts, string? LastError, DateTimeOffset? DeadLetteredAt);

public sealed record OutboxStats(int Pending, TimeSpan? OldestPendingAge, int DeadLettered, IReadOnlyList<OutboxMessageView> DeadLetters);

/// <summary>Operational facts for the system page. Implemented in Infrastructure; cross-tenant where noted.</summary>
public interface ISystemStatus
{
    /// <summary>Runs every registered health check.</summary>
    Task<HealthReportView> HealthAsync(CancellationToken cancellationToken = default);

    Task<DatabaseStats> DatabaseAsync(CancellationToken cancellationToken = default);

    /// <summary>Stored files by tenant and provider (all tenants: storage is a platform concern).</summary>
    Task<IReadOnlyList<StorageUsage>> StorageAsync(CancellationToken cancellationToken = default);

    Task<OutboxStats> OutboxAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts a dead-lettered outbox message back in the queue for another round of attempts.</summary>
    Task<bool> RetryOutboxMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
}
