using Base.Application.Integrations;

namespace Base.UnitTests.Security;

public sealed class ApiKeysTests
{
    [Fact]
    public void Generated_keys_parse_hash_and_match_and_are_unique()
    {
        var (key, prefix, hash) = ApiKeys.Generate();

        ApiKeys.PrefixOf(key).ShouldBe(prefix);
        hash.Length.ShouldBe(64);
        ApiKeys.Matches(key, hash).ShouldBeTrue();
        ApiKeys.Matches(key + "x", hash).ShouldBeFalse();
        ApiKeys.Mask(prefix).ShouldBe($"bk_{prefix}_…");
        Enumerable.Range(0, 200).Select(_ => ApiKeys.Generate().Key).Distinct(StringComparer.Ordinal).Count().ShouldBe(200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer something")]
    [InlineData("bk_short")]
    [InlineData("xx_abcdefgh_0123456789abcdef0123456789abcdef")]
    [InlineData("bk_ABCDEFGH_0123456789abcdef0123456789abcdef")]
    [InlineData("bk_abcdefgh-0123456789abcdef0123456789abcdef")]
    public void Anything_that_isnt_a_key_is_rejected_before_the_database(string? candidate) =>
        ApiKeys.PrefixOf(candidate).ShouldBeNull();
}
