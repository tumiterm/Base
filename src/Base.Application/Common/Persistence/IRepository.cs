using System.Linq.Expressions;

namespace Base.Application.Common.Persistence;

/// <summary>
/// Generic data access for one aggregate type. Repositories stage changes; only
/// <see cref="IUnitOfWork.SaveAsync"/> commits them. Query filters (soft delete, tenant) always apply.
/// </summary>
/// <remarks>
/// Add a specialised repository (<c>IInvoiceRepository : IRepository&lt;Invoice&gt;</c>) only when it adds real
/// queries; register it in Infrastructure's DI and inject it directly.
/// </remarks>
public interface IRepository<T>
    where T : class
{
    Task<T?> GetByIdAsync(object id, CancellationToken cancellationToken = default);

    Task<T?> GetAsync(
        Expression<Func<T, bool>> filter,
        string[]? includeProperties = null,
        bool asNoTracking = false,
        CancellationToken cancellationToken = default);

    Task<T?> GetAsync(
        Expression<Func<T, bool>> filter,
        Expression<Func<T, object?>>[] includes,
        bool asNoTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>Projects a single row in the database (no tracking, only the selected columns are read).</summary>
    Task<TResult?> GetAsync<TResult>(
        Expression<Func<T, bool>> filter,
        Expression<Func<T, TResult>> selector,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> GetAllAsync(
        Expression<Func<T, bool>>? filter = null,
        string[]? includeProperties = null,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
        int? skip = null,
        int? take = null,
        bool asNoTracking = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> GetAllAsync(
        Expression<Func<T, bool>>? filter,
        Expression<Func<T, object?>>[] includes,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
        bool asNoTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>Projects rows in the database (no tracking, only the selected columns are read).</summary>
    Task<IReadOnlyList<TResult>> GetAllAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        Expression<Func<T, bool>>? filter = null,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default);

    /// <summary>Database-side paging: one COUNT plus one page query. <paramref name="orderBy"/> is required for stable pages.</summary>
    Task<PagedResult<TResult>> GetPagedAsync<TResult>(
        PageRequest page,
        Expression<Func<T, TResult>> selector,
        Func<IQueryable<T>, IOrderedQueryable<T>> orderBy,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken cancellationToken = default);

    Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken cancellationToken = default);

    Task<T> AddAsync(T entity, CancellationToken cancellationToken = default);

    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    void Update(T entity);

    void Attach(T entity);

    /// <summary>Soft-deletes <see cref="Domain.Common.ISoftDeletable"/> entities; hard-deletes anything else. Does not save.</summary>
    void Remove(T entity);

    /// <summary>Physically deletes the row even if the entity is soft-deletable. Use deliberately. Does not save.</summary>
    void HardDelete(T entity);
}
