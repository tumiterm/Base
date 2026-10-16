using System.Linq.Expressions;
using Base.Application.Common.Persistence;
using Base.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IRepository{T}"/>. Stages changes only; never calls SaveChanges.
/// Specialised repositories inherit from this and add queries through <see cref="Query"/>.
/// </summary>
internal class Repository<T>(AppDbContext context) : IRepository<T>
    where T : class
{
    protected AppDbContext Context { get; } = context;

    protected DbSet<T> Set { get; } = context.Set<T>();

    public async Task<T?> GetByIdAsync(object id, CancellationToken cancellationToken = default) =>
        await Set.FindAsync([id], cancellationToken);

    public Task<T?> GetAsync(Expression<Func<T, bool>> filter, string[]? includeProperties = null, bool asNoTracking = false, CancellationToken cancellationToken = default) =>
        Query(filter, includeProperties, asNoTracking).FirstOrDefaultAsync(cancellationToken);

    public Task<T?> GetAsync(Expression<Func<T, bool>> filter, Expression<Func<T, object?>>[] includes, bool asNoTracking = false, CancellationToken cancellationToken = default) =>
        GetAsync(filter, IncludePaths.From(includes), asNoTracking, cancellationToken);

    public Task<TResult?> GetAsync<TResult>(Expression<Func<T, bool>> filter, Expression<Func<T, TResult>> selector, CancellationToken cancellationToken = default) =>
        Query(filter, null, asNoTracking: true).Select(selector).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<T>> GetAllAsync(
        Expression<Func<T, bool>>? filter = null,
        string[]? includeProperties = null,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
        int? skip = null,
        int? take = null,
        bool asNoTracking = false,
        CancellationToken cancellationToken = default) =>
        await Window(Query(filter, includeProperties, asNoTracking), orderBy, skip, take).ToListAsync(cancellationToken);

    public Task<IReadOnlyList<T>> GetAllAsync(
        Expression<Func<T, bool>>? filter,
        Expression<Func<T, object?>>[] includes,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
        bool asNoTracking = false,
        CancellationToken cancellationToken = default) =>
        GetAllAsync(filter, IncludePaths.From(includes), orderBy, null, null, asNoTracking, cancellationToken);

    public async Task<IReadOnlyList<TResult>> GetAllAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        Expression<Func<T, bool>>? filter = null,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default) =>
        await Window(Query(filter, null, asNoTracking: true), orderBy, skip, take).Select(selector).ToListAsync(cancellationToken);

    public async Task<PagedResult<TResult>> GetPagedAsync<TResult>(
        PageRequest page,
        Expression<Func<T, TResult>> selector,
        Func<IQueryable<T>, IOrderedQueryable<T>> orderBy,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(orderBy);

        var query = Query(filter, null, asNoTracking: true);
        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResult<TResult>.Empty(page);
        }

        var items = await orderBy(query).Skip(page.Skip).Take(page.PageSize).Select(selector).ToListAsync(cancellationToken);
        return new PagedResult<TResult>(items, total, page.Page, page.PageSize);
    }

    public Task<bool> ExistsAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken cancellationToken = default) =>
        predicate is null ? Set.AnyAsync(cancellationToken) : Set.AnyAsync(predicate, cancellationToken);

    public Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null, CancellationToken cancellationToken = default) =>
        predicate is null ? Set.CountAsync(cancellationToken) : Set.CountAsync(predicate, cancellationToken);

    public async Task<T> AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await Set.AddAsync(entity, cancellationToken);
        return entity;
    }

    public Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default) =>
        Set.AddRangeAsync(entities, cancellationToken);

    public void Update(T entity) => Set.Update(entity);

    public void Attach(T entity) => Set.Attach(entity);

    public void Remove(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity is ISoftDeletable softDeletable)
        {
            // The auditing interceptor stamps DateDeleted/UserDeleted.
            softDeletable.IsDeleted = true;
            if (Context.Entry(entity).State == EntityState.Detached)
            {
                Set.Update(entity);
            }

            return;
        }

        Set.Remove(entity);
    }

    public void HardDelete(T entity) => Set.Remove(entity);

    protected IQueryable<T> Query(Expression<Func<T, bool>>? filter, string[]? includeProperties, bool asNoTracking)
    {
        IQueryable<T> query = Set;
        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        if (includeProperties is { Length: > 0 })
        {
            query = includeProperties.Aggregate(query, static (q, path) => q.Include(path));
        }

        return filter is null ? query : query.Where(filter);
    }

    private static IQueryable<T> Window(IQueryable<T> query, Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy, int? skip, int? take)
    {
        if (orderBy is not null)
        {
            query = orderBy(query);
        }

        if (skip is > 0)
        {
            query = query.Skip(skip.Value);
        }

        return take is > 0 ? query.Take(take.Value) : query;
    }
}
