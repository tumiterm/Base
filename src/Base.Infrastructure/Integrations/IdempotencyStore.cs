using Base.Application.Common.Abstractions;
using Base.Application.Integrations;
using Base.Application.Operations;
using Base.Application.Settings;
using Base.Domain.Common;
using Base.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Integrations;

/// <summary>A remembered API response. Mechanics, not business data: not audited, deleted when it expires.</summary>
public sealed class IdempotencyRecord
{
    public const int KeyMaxLength = 100;
    public const int CallerMaxLength = 100;
    public const int HashLength = 64;
    public const int ContentTypeMaxLength = 100;

    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    /// <summary>"client:{id}" or "user:{id}": keys are only unique per caller.</summary>
    public string Caller { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    /// <summary>SHA-256 of method, path and body: the same key with a different request is an error, not a replay.</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>0 while the first request is still running.</summary>
    public int StatusCode { get; set; }

    public string? ContentType { get; set; }

    public byte[]? Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class IdempotencyStore(AppDbContext db, ITenantContext tenant, IApplicationSettingService settings, TimeProvider time) : IIdempotencyStore
{
    public async Task<IdempotencyClaim> BeginAsync(string caller, string key, string requestHash, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        Guid? tenantId = tenant.IsResolved ? tenant.TenantId : null;
        var existing = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Caller == caller && r.Key == key, cancellationToken);
        if (existing is not null && existing.ExpiresAt <= now)
        {
            db.IdempotencyRecords.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
            existing = null;
        }

        if (existing is not null)
        {
            return Describe(existing, requestHash);
        }

        var record = new IdempotencyRecord
        {
            Id = SequentialGuid.Next(),
            TenantId = tenantId,
            Caller = caller,
            Key = key,
            RequestHash = requestHash,
            CreatedAt = now,
            ExpiresAt = now.AddHours(await settings.GetIntAsync(SettingCodes.Api.IdempotencyHours, cancellationToken)),
        };
        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new IdempotencyClaim(IdempotencyOutcome.Started, record.Id);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Two retries raced; the other one won.
            db.Entry(record).State = EntityState.Detached;
            var winner = await db.IdempotencyRecords.AsNoTracking().FirstAsync(r => r.TenantId == tenantId && r.Caller == caller && r.Key == key, cancellationToken);
            return Describe(winner, requestHash);
        }
    }

    public async Task CompleteAsync(Guid recordId, int statusCode, string? contentType, byte[] body, CancellationToken cancellationToken = default)
    {
        var record = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Id == recordId, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.StatusCode = statusCode;
        record.ContentType = contentType is { Length: > IdempotencyRecord.ContentTypeMaxLength } ? contentType[..IdempotencyRecord.ContentTypeMaxLength] : contentType;
        record.Body = body;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AbandonAsync(Guid recordId, CancellationToken cancellationToken = default) =>
        await db.IdempotencyRecords.Where(r => r.Id == recordId).ExecuteDeleteAsync(cancellationToken);

    private static IdempotencyClaim Describe(IdempotencyRecord record, string requestHash) =>
        !string.Equals(record.RequestHash, requestHash, StringComparison.Ordinal) ? new IdempotencyClaim(IdempotencyOutcome.Mismatch)
        : record.StatusCode == 0 ? new IdempotencyClaim(IdempotencyOutcome.InProgress)
        : new IdempotencyClaim(IdempotencyOutcome.Replay, record.Id, record.StatusCode, record.ContentType, record.Body);
}

/// <summary>Deletes remembered responses whose window has passed.</summary>
internal sealed class IdempotencyCleanupJob(AppDbContext db, TimeProvider time) : IScheduledJob
{
    public string Code => "idempotency-cleanup";

    public string Name => "Idempotency cleanup";

    public string Description => "Deletes API responses kept for Idempotency-Key replays once Api.IdempotencyHours has passed.";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var deleted = await db.IdempotencyRecords.Where(r => r.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        return $"Removed {deleted} expired response(s).";
    }
}
