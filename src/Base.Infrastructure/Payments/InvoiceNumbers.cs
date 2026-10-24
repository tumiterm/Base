using Base.Application.Payments;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Payments;

/// <summary>
/// The highest invoice number plus one, read with a lock held to the end of the transaction, so two invoices issued at
/// once wait for each other instead of taking the same number (the unique index is the backstop).
/// </summary>
internal sealed class InvoiceNumbers(AppDbContext db) : IInvoiceNumbers
{
    public async Task<long> NextAsync(CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Take an invoice number inside the transaction that saves the invoice.");
        }

        return await db.Database
            .SqlQuery<long>($"SELECT ISNULL(MAX([Sequence]), 0) + 1 AS [Value] FROM [Invoices] WITH (UPDLOCK, HOLDLOCK)")
            .SingleAsync(cancellationToken);
    }
}
