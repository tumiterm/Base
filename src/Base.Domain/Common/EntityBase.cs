namespace Base.Domain.Common;

/// <summary>
/// The only entity base in the solution: identity, audit stamps, soft delete, row version and domain events.
/// Equality is by identity; two transient entities (default Id) are never equal.
/// </summary>
public abstract class EntityBase<TKey> : IIdentifiable<TKey>, IAuditable, ISoftDeletable, IHasRowVersion, IHasDomainEvents
    where TKey : IEquatable<TKey>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public TKey Id { get; set; } = default!;

    public DateTimeOffset DateCreated { get; set; }

    public DateTimeOffset DateModified { get; set; }

    public string? UserCreated { get; set; }

    public string? UserModified { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DateDeleted { get; set; }

    public string? UserDeleted { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public bool IsTransient => Id is null || Id.Equals(default!);

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    public override bool Equals(object? obj)
    {
        if (obj is not EntityBase<TKey> other || other.GetType() != GetType())
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return !IsTransient && !other.IsTransient && Id.Equals(other.Id);
    }

    public override int GetHashCode() =>
        IsTransient ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this) : HashCode.Combine(GetType(), Id);

    public static bool operator ==(EntityBase<TKey>? left, EntityBase<TKey>? right) => Equals(left, right);

    public static bool operator !=(EntityBase<TKey>? left, EntityBase<TKey>? right) => !Equals(left, right);
}

/// <summary>The default entity base: <see cref="Guid"/> keys, assigned on add as SQL Server-sequential GUIDs (see <see cref="SequentialGuid"/>).</summary>
public abstract class EntityBase : EntityBase<Guid>;
