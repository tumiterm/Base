using Base.Application.Common.Text;
using Base.Application.Documents;
using Base.Application.Documents.Generation;
using Base.Application.Files;

namespace Base.UnitTests.Documents;

public sealed class DocumentCatalogTests
{
    [Fact]
    public void Type_codes_are_unique_valid_file_categories()
    {
        DocumentTypeCatalog.All.Select(t => t.Code).ShouldBeUnique();
        DocumentTypeCatalog.All.ShouldAllBe(t => DocumentTypeCatalog.IsValidCode(t.Code));
        DocumentTypeCatalog.All.Where(t => t.AllowedExtensions != null).SelectMany(t => t.AllowedExtensions!).ShouldAllBe(e => e.StartsWith('.') && string.Equals(e, e.ToLowerInvariant(), StringComparison.Ordinal));
    }

    [Fact]
    public void Every_template_has_a_file_a_known_type_and_declares_exactly_the_variables_it_uses()
    {
        DocumentTemplateCatalog.All.Select(t => t.Code).ShouldBeUnique();
        DocumentTemplateFiles.Codes.ShouldBe(DocumentTemplateCatalog.All.Select(t => t.Code), ignoreOrder: true);

        foreach (var template in DocumentTemplateCatalog.All)
        {
            DocumentTypeCatalog.Find(template.DocumentType).ShouldNotBeNull(template.Code);
            var used = TemplateRenderer.VariablesIn(DocumentTemplateFiles.Find(template.Code)!);
            used.ShouldBe(template.Variables.Concat(DocumentTemplateCatalog.BuiltInVariables), ignoreOrder: true, customMessage: template.Code);
        }
    }

    [Theory]
    [InlineData("Offer: Jane / Smith?", "Offer_ Jane _ Smith_")]
    [InlineData("  report.  ", "report")]
    [InlineData("tab\there", "tab_here")]
    public void Generated_file_names_are_valid_on_every_os(string title, string expected) =>
        DocumentGenerator.SafeFileName(title).ShouldBe(expected);

    [Theory]
    [InlineData("avatar", "Database")]
    [InlineData("AVATAR", "Database")]
    [InlineData("letter", "S3")]
    [InlineData("invoice", null)]
    public void Category_providers_are_read_from_pairs(string category, string? expected) =>
        FileService.ProviderForCategory(["avatar = Database", "broken", "letter=S3", "empty="], category).ShouldBe(expected);

    [Fact]
    public void The_tenant_is_read_back_from_an_object_key()
    {
        var tenant = Guid.NewGuid();
        ObjectKeys.TenantOf(ObjectKeys.Build(tenant, "x", DateTimeOffset.UnixEpoch, Guid.NewGuid(), ".pdf")).ShouldBe(tenant);
        ObjectKeys.TenantOf("not-a-guid/file.pdf").ShouldBeNull();
        ObjectKeys.TenantOf("no-slash").ShouldBeNull();
    }
}
