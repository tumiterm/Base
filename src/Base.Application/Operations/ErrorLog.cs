using Base.Application.Common.Persistence;

namespace Base.Application.Operations;

/// <summary>An unhandled error, as recorded for the system page (the full stack trace is in <see cref="Details"/>).</summary>
public sealed record ErrorRecord(
    string Ticket,
    DateTimeOffset OccurredAt,
    string ExceptionType,
    string Message,
    string Method,
    string Path,
    string? User,
    Guid? TenantId,
    string? TraceId,
    string Fingerprint,
    string Details);

/// <summary>
/// Unhandled errors by ticket number, so support can find what a user's "quote reference E-..." was. Kept for
/// <c>System.ErrorRetentionDays</c>. Administrators see their tenant's errors and errors outside any tenant.
/// </summary>
public interface IErrorLog
{
    Task RecordAsync(ErrorRecord error, CancellationToken cancellationToken = default);

    /// <param name="search">A ticket, exception type or message fragment.</param>
    Task<PagedResult<ErrorRecord>> SearchAsync(string? search, PageRequest page, CancellationToken cancellationToken = default);

    Task<ErrorRecord?> GetAsync(string ticket, CancellationToken cancellationToken = default);
}
