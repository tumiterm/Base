using Base.Application.Common.Text;

namespace Base.UnitTests.Common;

public sealed class PlaceholderTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("N/A")]
    [InlineData(" none ")]
    [InlineData("-")]
    [InlineData("NULL")]
    [InlineData("Not Applicable")]
    public void Placeholders_become_null(string? value) => PlaceholderText.Clean(value).ShouldBeNull();

    [Theory]
    [InlineData("  Acme Ltd ", "Acme Ltd")]
    [InlineData("Nan", "Nan")]
    [InlineData("N/A Street", "N/A Street")]
    public void Real_values_are_trimmed_and_kept(string value, string expected) => PlaceholderText.Clean(value).ShouldBe(expected);
}
