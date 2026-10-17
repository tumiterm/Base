using System.Reflection;
using Base.Application.Subscriptions;

namespace Base.UnitTests.Settings;

public sealed class EntitlementCatalogTests
{
    [Fact]
    public void Feature_and_limit_codes_and_the_catalogue_match_both_ways()
    {
        var codes = new[] { typeof(FeatureCodes), typeof(LimitCodes) }
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        codes.Where(c => EntitlementCatalog.Find(c) is null).ShouldBeEmpty();
        EntitlementCatalog.All.Select(e => e.Code).Except(codes, StringComparer.Ordinal).ShouldBeEmpty();
        EntitlementCatalog.Features.ShouldAllBe(f => f.Code.StartsWith("Feature.", StringComparison.Ordinal) && (f.DefaultValue == 0 || f.DefaultValue == 1));
        EntitlementCatalog.Limits.ShouldAllBe(l => l.Code.StartsWith("Limit.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(LimitCodes.StorageMb, 10240L, "10,240 MB")]
    [InlineData(LimitCodes.Users, null, "Unlimited")]
    [InlineData(FeatureCodes.Documents, 1L, "Included")]
    public void Values_format_for_people(string code, long? value, string expected) =>
        EntitlementCatalog.Get(code).Format(value).ShouldBe(expected);
}
