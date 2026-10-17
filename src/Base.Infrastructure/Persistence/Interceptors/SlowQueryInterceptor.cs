using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Base.Infrastructure.Startup;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Logs commands slower than <c>Database:SlowQueryThresholdMs</c> with their SQL (parameter values are never logged)
/// and the trace id, and counts them on the <c>Base</c> meter (<c>base.db.slow_commands</c>) so dashboards can alert.
/// </summary>
internal sealed partial class SlowQueryInterceptor(IOptions<DatabaseOptions> options, ILogger<SlowQueryInterceptor> logger) : DbCommandInterceptor
{
    public const string MeterName = "Base";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> SlowCommands = Meter.CreateCounter<long>("base.db.slow_commands", description: "Database commands slower than the configured threshold.");

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return ValueTask.FromResult(result);
    }

    internal bool Check(DbCommand command, CommandExecutedEventData eventData)
    {
        var threshold = options.Value.SlowQueryThresholdMs;
        if (threshold <= 0 || eventData.Duration.TotalMilliseconds < threshold)
        {
            return false;
        }

        SlowCommands.Add(1);
        LogSlow(logger, (long)eventData.Duration.TotalMilliseconds, threshold, Activity.Current?.TraceId.ToString(), command.CommandText);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Slow database command: {DurationMs} ms (threshold {ThresholdMs} ms), trace {TraceId}\n{Sql}")]
    private static partial void LogSlow(ILogger logger, long durationMs, int thresholdMs, string? traceId, string sql);
}
