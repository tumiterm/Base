using Base.Application.Common.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Base.Infrastructure.Persistence;

internal sealed class UnitOfWork(AppDbContext context, IServiceProvider services) : IUnitOfWork
{
    private readonly Dictionary<Type, object> _repositories = [];

    public IRepository<T> Repository<T>()
        where T : class
    {
        if (!_repositories.TryGetValue(typeof(T), out var repository))
        {
            // Resolved from DI so a registered specialised repository wins over the generic one.
            repository = services.GetRequiredService<IRepository<T>>();
            _repositories[typeof(T)] = repository;
        }

        return (IRepository<T>)repository;
    }

    public Task<int> SaveAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);

    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
        ExecuteInTransactionAsync<bool>(
            async ct =>
            {
                await operation(ct);
                return true;
            },
            cancellationToken);

    public Task<TResult> ExecuteInTransactionAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var strategy = context.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(
            (context, operation),
            static async (_, state, ct) =>
            {
                await using var transaction = await state.context.Database.BeginTransactionAsync(ct);
                try
                {
                    var result = await state.operation(ct);
                    await state.context.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return result;
                }
                catch
                {
                    // A retry re-runs the whole operation, so start it from a clean change tracker.
                    state.context.ChangeTracker.Clear();
                    throw;
                }
            },
            verifySucceeded: null,
            cancellationToken);
    }
}
