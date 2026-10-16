namespace Base.Infrastructure.Persistence.Conventions;

/// <summary>
/// Names of the global query filters. EF Core 10 named filters can be lifted individually:
/// <c>query.IgnoreQueryFilters([QueryFilters.SoftDelete])</c> shows retired rows but keeps tenant isolation.
/// </summary>
public static class QueryFilters
{
    public const string SoftDelete = "SoftDelete";
    public const string Tenant = "Tenant";
}
