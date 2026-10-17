using Base.Application.Files;

namespace Base.UnitTests.Files;

public sealed class FilePreviewsTests
{
    [Theory]
    [InlineData("application/pdf", PreviewKind.Pdf)]
    [InlineData("image/png", PreviewKind.Image)]
    [InlineData("IMAGE/JPEG", PreviewKind.Image)]
    [InlineData("text/csv; charset=utf-8", PreviewKind.Text)]
    [InlineData("text/html", PreviewKind.Html)]
    [InlineData("video/mp4", PreviewKind.Video)]
    [InlineData("image/svg+xml", PreviewKind.None)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", PreviewKind.None)]
    [InlineData(null, PreviewKind.None)]
    public void Only_types_a_browser_can_show_safely_are_previewed(string? contentType, PreviewKind expected) =>
        FilePreviews.KindOf(contentType).ShouldBe(expected);

    [Fact]
    public void Text_like_files_are_served_as_plain_text_so_they_are_never_interpreted()
    {
        FilePreviews.ServedAs(PreviewKind.Text, "application/xml").ShouldBe("text/plain; charset=utf-8");
        FilePreviews.ServedAs(PreviewKind.Image, "IMAGE/PNG").ShouldBe("image/png");
    }
}
