namespace Base.Domain.Common;

/// <summary>An object with a stable identity.</summary>
public interface IIdentifiable<TKey>
    where TKey : IEquatable<TKey>
{
    TKey Id { get; }
}
