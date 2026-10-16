namespace Base.Domain.Common;

/// <summary>
/// Opt-in business code + display name. Only entities that genuinely have both implement it
/// (FO forced Code/Name onto every table).
/// </summary>
public interface INamedEntity
{
    const int CodeMaxLength = 50;
    const int NameMaxLength = 250;

    string Code { get; }

    string Name { get; }
}
