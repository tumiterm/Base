using System.Collections.Frozen;

namespace Base.Application.MasterData;

/// <summary>
/// Lookup list codes. A list = a constant here + a <see cref="LookupCatalog"/> entry with its starting values. Every
/// tenant gets the starting values (added on startup and when a tenant is created) and maintains its own list after that.
/// </summary>
public static class LookupLists
{
    public static class People
    {
        public const string Title = "People.Title";
        public const string Department = "People.Department";
        public const string Skill = "People.Skill";
    }
}

/// <summary>A starting value: its code is stable, so code may refer to it (e.g. a "left" reason the workflow checks).</summary>
public sealed record LookupDefault(string Code, string Name);

/// <param name="AllowTenantValues">False: the list is fixed to its defaults (they can still be renamed and hidden).</param>
public sealed record LookupListDefinition(
    string Code,
    string Name,
    string Module,
    string Description,
    IReadOnlyList<LookupDefault> Defaults,
    bool AllowTenantValues = true);

public static class LookupCatalog
{
    public static IReadOnlyList<LookupListDefinition> All { get; } =
    [
        new(LookupLists.People.Title, "Titles", "People", "Forms of address shown before a person's name.",
            [new("mr", "Mr"), new("mrs", "Mrs"), new("ms", "Ms"), new("mx", "Mx"), new("dr", "Dr"), new("prof", "Prof")]),
        new(LookupLists.People.Department, "Departments", "People", "Where people work in the organisation.",
            [new("finance", "Finance"), new("hr", "Human resources"), new("it", "Information technology"), new("operations", "Operations"), new("sales", "Sales & marketing")]),
        new(LookupLists.People.Skill, "Skills", "People", "Skills people can be tagged with, to find the right person.",
            [new("leadership", "Leadership"), new("project-management", "Project management"), new("data-analysis", "Data analysis"), new("customer-service", "Customer service"), new("first-aid", "First aid")]),
    ];

    private static readonly FrozenDictionary<string, LookupListDefinition> ByCode = All.ToFrozenDictionary(l => l.Code, StringComparer.Ordinal);

    public static LookupListDefinition? Find(string code) => ByCode.GetValueOrDefault(code);

    public static LookupListDefinition Get(string code) =>
        Find(code) ?? throw new KeyNotFoundException($"Lookup list '{code}' is not in the LookupCatalog.");
}
