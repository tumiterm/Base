using System.Security.Cryptography;
using System.Text;
using Base.Domain.Integrations;

namespace Base.Application.Integrations;

/// <summary>
/// API keys look like <c>bk_{8-character prefix}_{32-character secret}</c>. The prefix finds the client; the whole key is
/// checked against its SHA-256 hash in constant time. The secret has about 190 bits of entropy, so a slow hash adds nothing.
/// </summary>
public static class ApiKeys
{
    public const string Scheme = "bk";
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string PrefixAlphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const int SecretLength = 32;

    public static (string Key, string Prefix, string Hash) Generate()
    {
        var prefix = RandomString(ApiClient.PrefixLength, PrefixAlphabet);
        var key = $"{Scheme}_{prefix}_{RandomString(SecretLength, Alphabet)}";
        return (key, prefix, Hash(key));
    }

    /// <summary>The prefix of a well-formed key, or null for anything else (cheap: no database work for junk).</summary>
    public static string? PrefixOf(string? key)
    {
        if (key is null || key.Length != Scheme.Length + 1 + ApiClient.PrefixLength + 1 + SecretLength || !key.StartsWith(Scheme + "_", StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = key.Substring(Scheme.Length + 1, ApiClient.PrefixLength);
        return key[Scheme.Length + 1 + ApiClient.PrefixLength] == '_' && prefix.All(c => PrefixAlphabet.Contains(c, StringComparison.Ordinal)) ? prefix : null;
    }

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool Matches(string key, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(key)), Encoding.ASCII.GetBytes(expectedHash));

    /// <summary>What the UI shows instead of the key: <c>bk_ab12cd34_…</c>.</summary>
    public static string Mask(string prefix) => $"{Scheme}_{prefix}_…";

    private static string RandomString(int length, string alphabet) =>
        string.Create(length, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
}
