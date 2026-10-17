using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Operations;
using Base.Domain.Common;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Operations;

internal sealed class ErrorLog(AppDbContext db, ITenantContext tenant) : IErrorLog
{
    public async Task RecordAsync(ErrorRecord error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        db.ErrorLogEntries.Add(new ErrorLogEntry
        {
            Id = SequentialGuid.Next(),
            Ticket = Cut(error.Ticket, ErrorLogEntry.TicketMaxLength),
            OccurredAt = error.OccurredAt,
            ExceptionType = Cut(error.ExceptionType, ErrorLogEntry.TypeMaxLength),
            Message = Cut(error.Message, ErrorLogEntry.MessageMaxLength),
            Method = Cut(error.Method, 16),
            Path = Cut(error.Path, ErrorLogEntry.PathMaxLength),
            User = error.User is null ? null : Cut(error.User, 256),
            TenantId = error.TenantId,
            TraceId = error.TraceId is null ? null : Cut(error.TraceId, 64),
            Fingerprint = Cut(error.Fingerprint, 32),
            Details = Cut(error.Details, ErrorLogEntry.DetailsMaxLength),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<ErrorRecord>> SearchAsync(string? search, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var query = Visible().Where(e => term == null || e.Ticket == term || e.ExceptionType.Contains(term) || e.Message.Contains(term));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<ErrorRecord>(items.Select(ToRecord).ToList(), total, page.Page, page.PageSize);
    }

    public async Task<ErrorRecord?> GetAsync(string ticket, CancellationToken cancellationToken = default)
    {
        var entry = await Visible().Where(e => e.Ticket == ticket).OrderByDescending(e => e.OccurredAt).FirstOrDefaultAsync(cancellationToken);
        return entry is null ? null : ToRecord(entry);
    }

    /// <summary>This tenant's errors and those that happened outside any tenant (before it was resolved); everything in system scope.</summary>
    private IQueryable<ErrorLogEntry> Visible()
    {
        var tenantId = tenant.TenantId;
        var all = tenant.IsSystemScope;
        return db.ErrorLogEntries.AsNoTracking().Where(e => all || e.TenantId == null || e.TenantId == tenantId);
    }

    private static ErrorRecord ToRecord(ErrorLogEntry e) =>
        new(e.Ticket, e.OccurredAt, e.ExceptionType, e.Message, e.Method, e.Path, e.User, e.TenantId, e.TraceId, e.Fingerprint, e.Details);

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
