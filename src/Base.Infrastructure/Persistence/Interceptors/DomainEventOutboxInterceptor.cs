using System.Text.Json;
using Base.Application.Common.Abstractions;
using Base.Domain.Common;
using Base.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Base.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Moves raised domain events into <see cref="OutboxMessage"/> rows in the same SaveChanges, so an event is
/// stored if and only if the change that raised it commits. The outbox processor dispatches them afterwards.
/// </summary>
internal sealed class DomainEventOutboxInterceptor(ITenantContext tenant, TimeProvider time) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var sources = context.ChangeTracker.Entries<IHasDomainEvents>()
            .Select(e => e.Entity)
            .Where(e => e.DomainEvents.Count > 0)
            .ToList();

        if (sources.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        Guid? tenantId = tenant.IsResolved ? tenant.TenantId : null;

        foreach (var source in sources)
        {
            foreach (var domainEvent in source.DomainEvents)
            {
                var type = domainEvent.GetType();
                context.Add(new OutboxMessage
                {
                    Id = SequentialGuid.Next(),
                    Type = OutboxMessage.TypeNameOf(type),
                    Payload = JsonSerializer.Serialize(domainEvent, type, OutboxMessage.JsonOptions),
                    TenantId = tenantId,
                    OccurredAt = now,
                    NextAttemptAt = now,
                });
            }

            source.ClearDomainEvents();
        }
    }
}
