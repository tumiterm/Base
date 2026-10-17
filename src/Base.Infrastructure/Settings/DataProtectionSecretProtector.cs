using System.Security.Cryptography;
using Base.Application.Common.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Base.Infrastructure.Settings;

/// <summary>
/// Secrets encrypted with Data Protection under their own purpose. The key ring lives in the database; protect it
/// at rest in production (<c>ProtectKeysWithCertificate</c> / Azure Key Vault) so a database copy alone can't decrypt.
/// </summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Base.Settings.Secret.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            return null; // The key that encrypted it is gone; the operator has to enter the secret again.
        }
    }
}
