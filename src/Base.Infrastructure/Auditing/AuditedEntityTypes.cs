using Base.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Base.Infrastructure.Auditing;

/// <summary>The audited entity type names, from the EF model, for filter lists. Computed once.</summary>
public sealed class AuditedEntityTypes(IServiceScopeFactory scopes)
{
    private readonly Lazy<IReadOnlyList<string>> _names = new(() =>
    {
        using var scope = scopes.CreateScope();
        return AuditPolicy.AuditedNames(scope.ServiceProvider.GetRequiredService<AppDbContext>().Model).ToList();
    });

    public IReadOnlyList<string> Names => _names.Value;
}
