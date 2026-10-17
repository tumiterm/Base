using System.Reflection;
using Base.Application.Settings;

namespace Base.UnitTests.Settings;

public sealed class SettingCatalogTests
{
    private static readonly string[] Codes = typeof(SettingCodes)
        .GetNestedTypes()
        .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void Codes_and_catalogue_match_both_ways()
    {
        Codes.Where(c => SettingCatalog.Find(c) is null).ShouldBeEmpty();
        SettingCatalog.All.Select(s => s.Code).Except(Codes, StringComparer.Ordinal).ShouldBeEmpty();
    }

    public static TheoryData<string> AllCodes() => new(SettingCatalog.All.Select(s => s.Code));

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Default_value_passes_its_own_validation(string code)
    {
        var definition = SettingCatalog.Get(code);

        var result = definition.Validate(definition.DefaultValue);

        result.IsSuccess.ShouldBeTrue(result.Message);
    }

    [Theory]
    [InlineData(SettingDataType.Integer, "abc", false)]
    [InlineData(SettingDataType.Integer, "5", true)]
    [InlineData(SettingDataType.Integer, "0", false)]
    [InlineData(SettingDataType.Integer, "11", false)]
    [InlineData(SettingDataType.Boolean, "maybe", false)]
    [InlineData(SettingDataType.Boolean, "true", true)]
    [InlineData(SettingDataType.TimeZone, "Mars/Olympus", false)]
    [InlineData(SettingDataType.TimeZone, "Africa/Johannesburg", true)]
    [InlineData(SettingDataType.Color, "#0f766E", true)]
    [InlineData(SettingDataType.Color, "#fff", false)]
    [InlineData(SettingDataType.Color, "red", false)]
    [InlineData(SettingDataType.Color, "#12345g", false)]
    public void Validate_checks_type_and_range(SettingDataType type, string value, bool valid)
    {
        var definition = new SettingDefinition("Test.Code", "Test", type, null, "Test", "Test", Min: 1, Max: 10);

        definition.Validate(value).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    public void Validate_enforces_allowed_values()
    {
        var definition = SettingCatalog.Get(SettingCodes.Files.DefaultProvider);

        definition.Validate("S3").IsSuccess.ShouldBeTrue();
        definition.Validate("Database").IsSuccess.ShouldBeFalse();
    }
}
