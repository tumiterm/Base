namespace Base.Application.Common.Persistence;

/// <summary>
/// The single commit point. Small on purpose: no property per table (FO's grew to 700+ lines).
/// </summary>
public interface IUnitOfWork
{
    /// <summary>The repository for <typeparamref name="T"/>, including a specialised one if registered.</summary>
    IRepository<T> Repository<T>()
        where T : class;

    Task<int> SaveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> inside a database transaction, retried as a whole on transient failures.
    /// Use this instead of hand-rolled BeginTransaction: with connection resiliency enabled, a user transaction
    /// outside the execution strategy is rejected by EF Core. The operation must be safe to re-run.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ExecuteInTransactionAsync(Func{CancellationToken, Task}, CancellationToken)"/>
    Task<TResult> ExecuteInTransactionAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default);
}
