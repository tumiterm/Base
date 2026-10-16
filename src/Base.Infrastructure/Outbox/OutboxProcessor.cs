using Base.Application.Common.Abstractions;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Outbox;

/// <summary>
/// Polls the outbox, claims due messages with a lease (safe with several app instances), dispatches them in their
/// tenant, and retries failures with exponential backoff until <see cref="OutboxOptions.MaxAttempts"/>, then dead-letters.
/// </summary>
internal sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    TimeProvider time,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private DateTimeOffset _nextCleanup;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.PollIntervalSeconds), time);

        do
        {
            try
            {
                while (await ProcessBatchAsync(settings, stoppingToken) == settings.BatchSize)
                {
                    // A full batch means there is probably more waiting: keep going without waiting for the timer.
                }

                await CleanupAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogBatchFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> ProcessBatchAsync(OutboxOptions settings, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        List<Guid> candidates;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            candidates = await db.OutboxMessages.AsNoTracking()
                .Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null && m.NextAttemptAt <= now && (m.LockedUntil == null || m.LockedUntil < now))
                .OrderBy(m => m.NextAttemptAt)
                .Select(m => m.Id)
                .Take(settings.BatchSize)
                .ToListAsync(cancellationToken);
        }

        foreach (var id in candidates)
        {
            await ProcessOneAsync(id, settings, cancellationToken);
        }

        return candidates.Count;
    }

    private async Task ProcessOneAsync(Guid id, OutboxOptions settings, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var lockUntil = now.AddSeconds(settings.LockSeconds);

        // Atomic claim: the WHERE is re-evaluated under the row lock, so only one instance wins.
        var claimed = await db.OutboxMessages
            .Where(m => m.Id == id && m.ProcessedAt == null && (m.LockedUntil == null || m.LockedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntil, lockUntil), cancellationToken);
        if (claimed == 0)
        {
            return;
        }

        var message = await db.OutboxMessages.SingleAsync(m => m.Id == id, cancellationToken);
        if (message.TenantId is { } tenantId)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, null);
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<DomainEventDispatcher>().DispatchAsync(message, cancellationToken);
            message.ProcessedAt = time.GetUtcNow();
            message.LastError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            message.Attempts++;
            message.LastError = Truncate(exception.ToString(), OutboxMessage.ErrorMaxLength);
            if (message.Attempts >= settings.MaxAttempts)
            {
                message.DeadLetteredAt = time.GetUtcNow();
                LogDeadLettered(logger, exception, message.Id, message.Type, message.Attempts);
            }
            else
            {
                var delay = TimeSpan.FromSeconds(Math.Min(3600, 5 * Math.Pow(2, message.Attempts - 1)));
                message.NextAttemptAt = time.GetUtcNow().Add(delay);
                LogRetry(logger, exception, message.Id, message.Type, message.Attempts, delay);
            }
        }

        message.LockedUntil = null;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task CleanupAsync(OutboxOptions settings, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now.AddHours(1);
        var cutoff = now.AddDays(-settings.RetentionDays);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.OutboxMessages.Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox batch failed")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) failed on attempt {Attempt}; retrying in {Delay}")]
    private static partial void LogRetry(ILogger logger, Exception exception, Guid messageId, string type, int attempt, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({Type}) dead-lettered after {Attempts} attempts")]
    private static partial void LogDeadLettered(ILogger logger, Exception exception, Guid messageId, string type, int attempts);
}
