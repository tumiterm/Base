using System.Reflection;
using Base.Application.Notifications;
using Base.Domain.Notifications;

namespace Base.UnitTests.Notifications;

public sealed class NotificationCatalogTests
{
    private static readonly string[] Codes = typeof(NotificationTemplates)
        .GetNestedTypes()
        .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    public static TheoryData<string> AllCodes() => new(NotificationCatalog.All.Select(t => t.Code));

    [Fact]
    public void Codes_and_catalogue_match_both_ways()
    {
        Codes.Where(c => NotificationCatalog.Find(c) is null).ShouldBeEmpty();
        NotificationCatalog.All.Select(t => t.Code).Except(Codes, StringComparer.Ordinal).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Every_template_has_a_file(string code) => NotificationTemplateFiles.Exists(code).ShouldBeTrue($"Add Notifications/Templates/{code}.txt");

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Every_channel_a_template_can_use_has_its_wording(string code)
    {
        var template = NotificationCatalog.Get(code);
        var parts = NotificationTemplateFiles.PartsOf(code);

        foreach (var channel in template.Channels.Concat(template.Fallback ?? []))
        {
            if (channel == NotificationChannel.WhatsApp)
            {
                template.WhatsApp.ShouldNotBeNull($"{code} sends on WhatsApp but has no approved template.");
            }
            else
            {
                TemplateParts.RequiredFor(channel).Where(p => !parts.ContainsKey(p)).ShouldBeEmpty($"{code} on {channel}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Templates_only_use_declared_or_built_in_variables(string code)
    {
        var template = NotificationCatalog.Get(code);
        var allowed = template.Variables.Concat(NotificationCatalog.BuiltInVariables).ToHashSet(StringComparer.Ordinal);

        NotificationTemplateFiles.PartsOf(code).Values.SelectMany(TemplateRenderer.VariablesIn).Where(v => !allowed.Contains(v)).ShouldBeEmpty();
        (template.WhatsApp?.Parameters ?? []).Where(v => !allowed.Contains(v)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Sensitive_templates_never_go_in_app_and_are_always_sent(string code)
    {
        var template = NotificationCatalog.Get(code);
        if (template.IsSensitive)
        {
            template.Channels.Concat(template.Fallback ?? []).ShouldNotContain(NotificationChannel.InApp, "In-app messages are stored; a sign-in link must not be.");
            template.IsMandatory.ShouldBeTrue();
        }
    }

    [Fact]
    public void Security_is_a_mandatory_category_and_reminders_are_not()
    {
        NotificationCatalog.IsMandatoryCategory(NotificationCategories.Security).ShouldBeTrue();
        NotificationCatalog.IsMandatoryCategory(NotificationCategories.Reminders).ShouldBeFalse();
    }
}

public sealed class TemplateRendererTests
{
    private static readonly Dictionary<string, string?> Values = new(StringComparer.Ordinal) { ["Name"] = "<Ann & Co>", ["Count"] = "3" };

    [Fact]
    public void Fills_placeholders_with_flexible_spacing() =>
        TemplateRenderer.Render("Hi {{Name}}, you have {{ Count }} items", Values, html: false).ShouldBe("Hi <Ann & Co>, you have 3 items");

    [Fact]
    public void Html_parts_are_encoded() =>
        TemplateRenderer.Render("<p>{{Name}}</p>", Values, html: true).ShouldBe("<p>&lt;Ann &amp; Co&gt;</p>");

    [Fact]
    public void An_unknown_variable_is_an_error_not_a_blank() =>
        Should.Throw<InvalidOperationException>(() => TemplateRenderer.Render("Dear {{Nmae}}", Values, html: false)).Message.ShouldContain("Nmae");

    [Fact]
    public void Lists_the_variables_a_template_uses() =>
        TemplateRenderer.VariablesIn("{{A}} {{ B }} {{A}} {not}").ShouldBe(["A", "B"], ignoreOrder: true);
}

public sealed class TemplateFileTests
{
    [Fact]
    public void Sections_are_split_and_trimmed()
    {
        var parts = NotificationTemplateFiles.Parse("--- email.subject\nHello\n--- sms\n\nLine one\nLine two\n");

        parts[TemplateParts.EmailSubject].ShouldBe("Hello");
        parts[TemplateParts.Sms].ShouldBe("Line one\nLine two");
    }

    [Fact]
    public void Unknown_sections_are_rejected() =>
        Should.Throw<FormatException>(() => NotificationTemplateFiles.Parse("--- email.subjct\nHi")).Message.ShouldContain("email.subjct");

    [Fact]
    public void Duplicate_sections_are_rejected() =>
        Should.Throw<FormatException>(() => NotificationTemplateFiles.Parse("--- sms\na\n--- sms\nb"));
}

public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("082 123 4567", "+27821234567")]
    [InlineData("+27 82 123 4567", "+27821234567")]
    [InlineData("0027821234567", "+27821234567")]
    [InlineData("(082) 123-4567", "+27821234567")]
    [InlineData("+1 415 555 0100", "+14155550100")]
    public void Numbers_become_e164(string raw, string expected) => PhoneNumbers.ToE164(raw, "27").ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("call me")]
    [InlineData("+0821234567")]
    public void Garbage_is_rejected(string? raw) => PhoneNumbers.ToE164(raw, "27").ShouldBeNull();
}
