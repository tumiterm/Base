using Base.Domain.Common;

namespace Base.Application.Messaging;

/// <summary>
/// Handles a domain event after the change that raised it has committed. Runs from the outbox processor, in the
/// tenant the event was raised in, with retries; handlers must therefore be idempotent.
/// </summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
