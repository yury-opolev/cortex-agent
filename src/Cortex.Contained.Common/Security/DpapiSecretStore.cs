using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Cortex.Contained.Common.Security;

/// <summary>
/// Uses Windows Data Protection API (DPAPI) to encrypt/decrypt secrets.
/// Scope: CurrentUser — only the Windows user that encrypted a value can decrypt it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <inheritdoc />
    public string Unprotect(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);

        var bytes = Convert.FromBase64String(ciphertext);
        var decrypted = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}
