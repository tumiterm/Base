using System.Diagnostics;
using Base.Application.Auditing;
using Base.Application.Common.Abstractions;
using Base.Domain.Auditing;
using Base.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Base.Infrastructure.Auditing;

/// <summary>
/// Writes an <see cref="AuditEntry"/> for every audited insert, update, soft delete, restore and hard delete, in the
/// same SaveChanges, so a change and its audit record commit or roll back together. Registered after the auditing
/// interceptor so ids, stamps and tenant ownership are final. Also makes audit entries append-only.
/// </summary>
/// <remarks>
/// Diffs come from the change tracker's original values. Entities loaded and changed in the same context (the normal
/// path, and how Identity's stores work) diff exactly. An entity attached detached and marked modified has no
/// originals; the change is recorded as an update without values.
/// </remarks>
internal sealed class AuditTrailInterceptor(ICurrentUser currentUser, ITenantContext tenant, TimeProvider time) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Record(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Record(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var entries = context.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (entries.Any(e => e.Entity is AuditEntry && e.State != EntityState.Added))
        {
            throw new InvalidOperationException("Audit entries are append-only; they cannot be changed or deleted.");
        }

        var now = time.GetUtcNow();
        var traceId = Activity.Current?.TraceId.ToString();
        var audits = new List<AuditEntry>();

        foreach (var entry in entries.Where(e => AuditPolicy.IsAudited(e.Entity.GetType())))
        {
            var (action, changes) = Describe(entry);
            if (action is null)
            {
                continue;
            }

            audits.Add(new AuditEntry(
                TenantOf(entry),
                AuditPolicy.NameOf(entry.Entity.GetType()),
                AuditPolicy.EntityIdOf(entry),
                action.Value,
                AuditChange.Serialize(changes),
                currentUser.UserId,
                currentUser.AuditName,
                traceId,
                now));
        }

        context.AddRange(audits);
    }

    private static (AuditAction? Action, List<AuditChange> Changes) Describe(EntityEntry entry)
    {
        var changes = new List<AuditChange>();
        switch (entry.State)
        {
            case EntityState.Added:
                foreach (var property in entry.Properties)
                {
                    AddValue(changes, property, old: null, current: property.CurrentValue);
                }

                return (AuditAction.Created, changes);

            case EntityState.Deleted:
                foreach (var property in entry.Properties)
                {
                    AddValue(changes, property, old: property.OriginalValue, current: null);
                }

                return (AuditAction.Purged, changes);

            default:
                foreach (var property in entry.Properties.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)))
                {
                    AddValue(changes, property, property.OriginalValue, property.CurrentValue);
                }

                var action = SoftDeleteTransition(entry) ?? AuditAction.Updated;
                return action == AuditAction.Updated && changes.Count == 0 ? (null, changes) : (action, changes);
        }
    }

    private static AuditAction? SoftDeleteTransition(EntityEntry entry)
    {
        if (entry.Entity is not ISoftDeletable)
        {
            return null;
        }

        var deleted = entry.Property(nameof(ISoftDeletable.IsDeleted));
        return (deleted.OriginalValue, deleted.CurrentValue) switch
        {
            (false, true) => AuditAction.Deleted,
            (true, false) => AuditAction.Restored,
            _ => null,
        };
    }

    private static void AddValue(List<AuditChange> changes, PropertyEntry property, object? old, object? current)
    {
        switch (AuditPolicy.RuleFor(property.Metadata))
        {
            case AuditPolicy.PropertyRule.Ignore:
                return;
            case AuditPolicy.PropertyRule.Redact:
                if (old is not null || current is not null)
                {
                    changes.Add(new AuditChange(property.Metadata.Name, null, null, Redacted: true));
                }

                return;
            default:
                if (old is not null || current is not null)
                {
                    changes.Add(new AuditChange(property.Metadata.Name, AuditPolicy.Format(old), AuditPolicy.Format(current)));
                }

                return;
        }
    }

    /// <summary>The owning tenant: the row's own tenant column if it has one (null = platform row), otherwise the acting tenant.</summary>
    private Guid? TenantOf(EntityEntry entry)
    {
        if (entry.Metadata.FindProperty(nameof(ITenantOwned.TenantId)) is { } property)
        {
            return entry.Property(property.Name).CurrentValue switch
            {
                Guid id when id != Guid.Empty => id,
                Guid => null,
                null => null,
                var other => (Guid?)other,
            };
        }

        return tenant.IsResolved ? tenant.TenantId : null;
    }
}
