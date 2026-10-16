using Base.Application.Files;

namespace Base.UnitTests.Files;

public sealed class FileSignaturesTests
{
    [Fact]
    public void Real_pdf_matches() => FileSignatures.Matches(".pdf", "%PDF-1.7\n"u8).ShouldBeTrue();

    [Fact]
    public void Executable_renamed_to_pdf_is_rejected() => FileSignatures.Matches(".pdf", "MZ\x90\0"u8).ShouldBeFalse();

    [Fact]
    public void Png_signature_is_checked()
    {
        FileSignatures.Matches(".png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]).ShouldBeTrue();
        FileSignatures.Matches(".PNG", "GIF89a"u8).ShouldBeFalse();
    }

    [Fact]
    public void Office_documents_are_zip_containers() => FileSignatures.Matches(".docx", [0x50, 0x4B, 0x03, 0x04, 0x14]).ShouldBeTrue();

    [Fact]
    public void Short_header_does_not_match() => FileSignatures.Matches(".png", [0x89, 0x50]).ShouldBeFalse();

    [Fact]
    public void Extensions_without_signature_are_accepted() => FileSignatures.Matches(".csv", "a,b\n"u8).ShouldBeTrue();

    [Fact]
    public void Content_type_comes_from_extension_not_client() =>
        FileSignatures.ContentTypeFor(".xlsx").ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
}

public sealed class ObjectKeysTests
{
    [Fact]
    public void Key_is_tenant_category_date_and_id_never_the_file_name()
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var file = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var key = ObjectKeys.Build(tenant, "invoice", new DateTimeOffset(2026, 3, 9, 23, 30, 0, TimeSpan.FromHours(-2)), file, ".PDF");

        key.ShouldBe("11111111111111111111111111111111/invoice/2026/03/22222222222222222222222222222222.pdf");
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("Invoice")]
    [InlineData("")]
    [InlineData("a/b")]
    public void Unsafe_categories_are_rejected(string category) =>
        Should.Throw<ArgumentException>(() => ObjectKeys.Build(Guid.NewGuid(), category, DateTimeOffset.UnixEpoch, Guid.NewGuid(), ".pdf"));

    [Fact]
    public void Unsafe_extensions_are_rejected() =>
        Should.Throw<ArgumentException>(() => ObjectKeys.Build(Guid.NewGuid(), "x", DateTimeOffset.UnixEpoch, Guid.NewGuid(), "./../a"));
}
