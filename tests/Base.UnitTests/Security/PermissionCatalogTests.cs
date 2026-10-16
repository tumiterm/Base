using System.Reflection;
using Base.Application.Security;

namespace Base.UnitTests.Security;

public sealed class PermissionCatalogTests
{
    private static readonly string[] Codes = typeof(PermissionCodes)
        .GetNestedTypes()
        .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void Every_code_is_catalogued() =>
        Codes.Where(c => !PermissionCatalog.Exists(c)).ShouldBeEmpty();

    [Fact]
    public void Every_catalogue_entry_has_a_code() =>
        PermissionCatalog.All.Select(p => p.Key).Except(Codes, StringComparer.Ordinal).ShouldBeEmpty();

    [Fact]
    public void Keys_are_unique() =>
        PermissionCatalog.All.GroupBy(p => p.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).ShouldBeEmpty();

    [Fact]
    public void Implied_permissions_exist() =>
        PermissionCatalog.All.SelectMany(p => p.Implies).Where(i => !PermissionCatalog.Exists(i)).ShouldBeEmpty();

    [Fact]
    public void Default_grants_name_seeded_roles() =>
        PermissionCatalog.All.SelectMany(p => p.GrantedByDefaultTo).Except(RoleNames.Seeded, StringComparer.Ordinal).ShouldBeEmpty();

    [Fact]
    public void Expand_adds_implied_permissions_transitively_and_drops_unknown_keys()
    {
        var expanded = PermissionCatalog.Expand([PermissionCodes.System.JobsManage, PermissionCodes.Settings.Edit, "Nope.Nothing"]);

        expanded.ShouldBe(
            [PermissionCodes.System.JobsManage, PermissionCodes.System.HealthView, PermissionCodes.Settings.Edit, PermissionCodes.Settings.View],
            ignoreOrder: true);
    }
}
