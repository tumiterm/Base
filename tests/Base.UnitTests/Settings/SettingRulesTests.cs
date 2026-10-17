using Base.Application.Operations;
using Base.Application.Settings;

namespace Base.UnitTests.Settings;

public sealed class SettingRulesTests
{
    [Fact]
    public void Secrets_are_platform_wide_optional_and_have_no_default()
    {
        var secrets = SettingCatalog.All.Where(s => s.IsSecret).ToList();
        secrets.ShouldNotBeEmpty();
        secrets.ShouldAllBe(s => s.Scope == SettingScope.Platform && !s.IsRequired && s.DefaultValue == null);
    }

    [Fact]
    public void Switches_default_to_a_boolean()
    {
        SettingCatalog.All.Where(s => s.DataType == SettingDataType.Boolean)
            .ShouldAllBe(s => s.DefaultValue == "true" || s.DefaultValue == "false");
    }

    [Theory]
    [InlineData("noreply@example.com", true)]
    [InlineData("not an address", false)]
    [InlineData("a@b", false)]
    public void Email_settings_take_only_addresses(string value, bool valid) =>
        SettingCatalog.Get(SettingCodes.Email.FromAddress).Validate(value).IsSuccess.ShouldBe(valid);

    [Fact]
    public void Secrets_are_limited_in_length()
    {
        var password = SettingCatalog.Get(SettingCodes.Email.SmtpPassword);
        password.Validate(new string('x', SettingDefinition.SecretMaxLength)).IsSuccess.ShouldBeTrue();
        password.Validate(new string('x', SettingDefinition.SecretMaxLength + 1)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void The_build_knows_its_version_and_its_base_version()
    {
        AppVersion.Version.ShouldMatch(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");
        AppVersion.BaseVersion.ShouldMatch(@"^\d+\.\d+\.\d+$");
        AppVersion.Describe().ShouldStartWith(AppVersion.Version);
    }
}
