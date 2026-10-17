using Base.Application.Security;
using Base.Web.Errors;
using Base.Web.Security;
using Base.Web.Theming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.Web;

public sealed class WebUnitTests
{
    [Theory]
    [InlineData("#facc15", "#0b0d12")]
    [InlineData("#1e3a8a", "#ffffff")]
    [InlineData("#ffffff", "#0b0d12")]
    public void Accent_text_picks_the_higher_contrast(string accent, string text)
    {
        ThemePalette.ContrastText(ThemePalette.Rgb.Parse(accent)).ShouldBe(text);
    }

    [Fact]
    public void Theme_css_derives_light_and_dark_ramps_and_rejects_bad_colours()
    {
        var css = ThemePalette.From("#0F766E").Css();

        css.ShouldContain("--accent: #0f766e;");
        css.ShouldContain("[data-bs-theme=\"dark\"]");
        ThemePalette.From("red; } body { display:none").Accent.ShouldBe("#4f5bd5");
    }

    [Fact]
    public void Sensitive_query_values_are_masked()
    {
        var redacted = RequestRedaction.RedactQuery(new QueryString("?page=2&token=abc&Password=x&returnUrl=%2Fhome&api_key=k"));

        redacted.ShouldBe("?page=2&token=***&Password=***&returnUrl=%2Fhome&api_key=***");
    }

    [Fact]
    public async Task Unknown_permission_policies_fail_loudly()
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        await Should.ThrowAsync<InvalidOperationException>(() => provider.GetPolicyAsync(PermissionCatalog.PolicyPrefix + "Setings.Edit"));
        (await provider.GetPolicyAsync(PermissionCatalog.PolicyPrefix + PermissionCodes.Settings.Edit)).ShouldNotBeNull();
    }

    [Fact]
    public void Same_failure_site_gives_same_fingerprint()
    {
        static Exception Throw(string message)
        {
            try
            {
                throw new InvalidOperationException(message);
            }
            catch (InvalidOperationException exception)
            {
                return exception;
            }
        }

        ErrorAlert.FingerprintOf(Throw("a")).ShouldBe(ErrorAlert.FingerprintOf(Throw("different message")));
    }
}
