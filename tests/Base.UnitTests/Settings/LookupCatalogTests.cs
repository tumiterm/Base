using System.Reflection;
using System.Text.RegularExpressions;
using Base.Application.MasterData;
using Base.Domain.MasterData;

namespace Base.UnitTests.Settings;

public sealed partial class LookupCatalogTests
{
    [Fact]
    public void List_codes_and_the_catalogue_match_both_ways_and_defaults_are_well_formed()
    {
        var codes = typeof(LookupLists).GetNestedTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        codes.Where(c => LookupCatalog.Find(c) is null).ShouldBeEmpty();
        LookupCatalog.All.Select(l => l.Code).Except(codes, StringComparer.Ordinal).ShouldBeEmpty();
        foreach (var list in LookupCatalog.All)
        {
            list.Defaults.Select(d => d.Code).ShouldBeUnique(list.Code);
            list.Defaults.ShouldAllBe(d => Code().IsMatch(d.Code) && d.Code.Length <= LookupValue.CodeMaxLength && d.Name.Length <= LookupValue.NameMaxLength);
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.None, 1000)]
    private static partial Regex Code();
}
