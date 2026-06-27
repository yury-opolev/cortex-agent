namespace Cortex.Contained.Common.Security;

/// <summary>
/// Abstraction for encrypting and decrypting secrets at rest.
/// </summary>
public interface ISecretStore
{
    /// <summary>Encrypts a plaintext value for storage.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a previously protected value.</summary>
    string Unprotect(string ciphertext);
}
