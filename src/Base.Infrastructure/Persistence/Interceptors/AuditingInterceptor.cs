using System.Collections.Concurrent;
using Base.Application.Common.Abstractions;
using Base.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Base.Infrastructure.Persistence.Interceptors;

/// <summary>
/// On every save: assigns sequential Guid v7 ids, stamps audit columns (UTC, from <see cref="TimeProvider"/>),
/// maintains soft-delete stamps, and enforces tenant ownership (stamp on insert, reject cross-tenant writes).
/// </summary>
internal sealed class AuditingInterceptor(ICurrentUser currentUser, ITenantContext tenant, TimeProvider time) : SaveChangesInterceptor
{
    private static readonly ConcurrentDictionary<Type, bool> SkipCache = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var user = currentUser.AuditName;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged || ShouldSkip(entry.Entity.GetType()))
            {
                continue;
            }

            EnforceTenant(entry);

            if (entry.State == EntityState.Added && entry.Entity is IIdentifiable<Guid>)
            {
                var id = entry.Property(nameof(IIdentifiable<Guid>.Id));
                if (id.CurrentValue is Guid guid && guid == Guid.Empty)
                {
                    id.CurrentValue = SequentialGuid.Next(); // normally already set by EF on Add
                }
            }

            if (entry.Entity is IAuditable auditable)
            {
                if (entry.State == EntityState.Added)
                {
                    auditable.DateCreated = now;
                    auditable.UserCreated = user;
                    auditable.DateModified = now;
                    auditable.UserModified = user;
                }
                else if (entry.State == EntityState.Modified)
                {
                    auditable.DateModified = now;
                    auditable.UserModified = user;
                    entry.Property(nameof(IAuditable.DateCreated)).IsModified = false;
                    entry.Property(nameof(IAuditable.UserCreated)).IsModified = false;
                }
            }

            if (entry.Entity is ISoftDeletable softDeletable && entry.State == EntityState.Modified
                && entry.Property(nameof(ISoftDeletable.IsDeleted)).IsModified)
            {
                softDeletable.DateDeleted = softDeletable.IsDeleted ? now : null;
                softDeletable.UserDeleted = softDeletable.IsDeleted ? user : null;
            }
        }
    }

    private void EnforceTenant(EntityEntry entry)
    {
        if (entry.Entity is not ITenantOwned owned || tenant.IsSystemScope)
        {
            return;
        }

        if (entry.State == EntityState.Added)
        {
            if (owned.TenantId == Guid.Empty)
            {
                owned.TenantId = tenant.IsResolved
                    ? tenant.TenantId
                    : throw new InvalidOperationException($"Cannot insert {entry.Entity.GetType().Name}: no tenant has been resolved for this operation.");
            }
            else if (owned.TenantId != tenant.TenantId)
            {
                throw new InvalidOperationException($"Cannot insert {entry.Entity.GetType().Name} for another tenant.");
            }

            return;
        }

        // Compare values, not IsModified: DbSet.Update() (used by Identity's stores) flags every column as modified.
        var tenantProperty = entry.Property(nameof(ITenantOwned.TenantId));
        if ((Guid)tenantProperty.CurrentValue! != tenant.TenantId || (Guid)tenantProperty.OriginalValue! != tenant.TenantId)
        {
            throw new InvalidOperationException($"Cross-tenant {entry.State} of {entry.Entity.GetType().Name} was blocked.");
        }
    }

    private static bool ShouldSkip(Type type) =>
        SkipCache.GetOrAdd(type, static t => Attribute.IsDefined(t, typeof(SkipAuditAttribute)));
}
