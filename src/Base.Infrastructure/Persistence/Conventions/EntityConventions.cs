using System.Linq.Expressions;
using Base.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Persistence.Conventions;

internal static class EntityConventions
{
    public const int AuditUserMaxLength = 256;

    private static readonly Type[] ConventionInterfaces =
    [
        typeof(IHasDomainEvents), typeof(IHasRowVersion), typeof(IAuditable), typeof(INamedEntity), typeof(ISoftDeletable), typeof(ITenantOwned),
    ];

    /// <summary>
    /// Applies the cross-cutting model rules by interface. Call once at the end of <c>OnModelCreating</c>.
    /// Soft delete and tenant isolation are separate <em>named</em> filters, so neither can silently replace the
    /// other (FO's single <c>HasQueryFilter</c> slot made that easy to get wrong).
    /// </summary>
    public static void ApplyEntityConventions(this ModelBuilder builder, AppDbContext context)
    {
        foreach (var entityType in builder.Model.GetEntityTypes().Where(t => !t.IsOwned()).ToList())
        {
            var clrType = entityType.ClrType;
            // Only touch types that opt in through an interface: calling Entity() on framework types (e.g. Identity's
            // passkey entity with its JSON complex property) can re-trigger discovery and break their mapping.
            if (Attribute.IsDefined(clrType, typeof(SkipAuditAttribute)) || !ConventionInterfaces.Any(i => i.IsAssignableFrom(clrType)))
            {
                continue;
            }

            var entity = builder.Entity(clrType);

            if (typeof(IHasDomainEvents).IsAssignableFrom(clrType))
            {
                entity.Ignore(nameof(IHasDomainEvents.DomainEvents));
            }

            if (typeof(IHasRowVersion).IsAssignableFrom(clrType))
            {
                entity.Property(nameof(IHasRowVersion.RowVersion)).IsRowVersion();
            }

            if (typeof(IAuditable).IsAssignableFrom(clrType))
            {
                entity.Property(nameof(IAuditable.DateCreated)).HasDefaultValueSql("SYSUTCDATETIME()");
                entity.Property(nameof(IAuditable.DateModified)).HasDefaultValueSql("SYSUTCDATETIME()");
                entity.Property(nameof(IAuditable.UserCreated)).HasMaxLength(AuditUserMaxLength);
                entity.Property(nameof(IAuditable.UserModified)).HasMaxLength(AuditUserMaxLength);
            }

            if (typeof(INamedEntity).IsAssignableFrom(clrType))
            {
                entity.Property(nameof(INamedEntity.Code)).HasMaxLength(INamedEntity.CodeMaxLength).IsRequired();
                entity.Property(nameof(INamedEntity.Name)).HasMaxLength(INamedEntity.NameMaxLength).IsRequired();
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                entity.Property(nameof(ISoftDeletable.UserDeleted)).HasMaxLength(AuditUserMaxLength);
                entity.HasQueryFilter(QueryFilters.SoftDelete, SoftDeleteFilter(clrType));
            }

            if (typeof(ITenantOwned).IsAssignableFrom(clrType))
            {
                entity.HasIndex(nameof(ITenantOwned.TenantId));
                entity.HasQueryFilter(QueryFilters.Tenant, TenantFilter(clrType, context));
            }
        }
    }

    // e => !e.IsDeleted
    private static LambdaExpression SoftDeleteFilter(Type type)
    {
        var e = Expression.Parameter(type, "e");
        return Expression.Lambda(Expression.Not(Expression.Property(e, nameof(ISoftDeletable.IsDeleted))), e);
    }

    // e => context.IsSystemScope || e.TenantId == context.CurrentTenantId
    // Fails closed: an unresolved tenant is Guid.Empty, which matches no rows.
    private static LambdaExpression TenantFilter(Type type, AppDbContext context)
    {
        var e = Expression.Parameter(type, "e");
        var ctx = Expression.Constant(context);
        var body = Expression.OrElse(
            Expression.Property(ctx, nameof(AppDbContext.IsSystemScope)),
            Expression.Equal(
                Expression.Property(e, nameof(ITenantOwned.TenantId)),
                Expression.Property(ctx, nameof(AppDbContext.CurrentTenantId))));
        return Expression.Lambda(body, e);
    }
}
