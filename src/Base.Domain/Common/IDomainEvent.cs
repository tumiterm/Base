namespace Base.Domain.Common;

/// <summary>
/// Something that happened in the domain. Raised by an entity, written to the outbox in the same
/// transaction as the change, and dispatched to handlers after commit.
/// </summary>
public interface IDomainEvent;

/// <summary>An entity that can raise domain events.</summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
