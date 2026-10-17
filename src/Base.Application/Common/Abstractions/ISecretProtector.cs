namespace Base.Application.Common.Abstractions;

/// <summary>
/// Encrypts small secrets for storage (ASP.NET Core Data Protection, keys in the database). Used for
/// <c>Secret</c> settings; anything a deployment owns belongs in configuration instead.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>The plaintext, or null when it can't be decrypted (keys lost or rotated out).</summary>
    string? Unprotect(string protectedValue);
}
