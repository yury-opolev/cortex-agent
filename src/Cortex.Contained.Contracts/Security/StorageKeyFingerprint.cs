using System.Security.Cryptography;

namespace Cortex.Contained.Contracts.Security;

/// <summary>
/// The non-secret identifier of a tenant storage master key: the first 8 bytes of
/// HKDF-SHA256(master, info <c>cortex-storage/key-check</c>) as 16 lowercase hex characters. The Bridge,
/// the agent and the recovery kit compare it to tell "the same key" from "another key" without revealing
/// the key itself.
/// </summary>
public static class StorageKeyFingerprint
{
    /// <summary>Required master key length in bytes.</summary>
    public const int KeyBytes = 32;

    /// <summary>The fingerprint of <paramref name="masterKey"/>.</summary>
    public static string Compute(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != KeyBytes)
        {
            throw new ArgumentException($"A storage key has exactly {KeyBytes} bytes.", nameof(masterKey));
        }

        Span<byte> check = stackalloc byte[8];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, check, ReadOnlySpan<byte>.Empty, "cortex-storage/key-check"u8);
        return Convert.ToHexStringLower(check);
    }

    /// <summary>The fingerprint of a Base64 master key.</summary>
    public static string Compute(string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        var key = Convert.FromBase64String(base64Key);
        try
        {
            return Compute(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Groups of four for display (<c>0123-4567-89ab-cdef</c>).</summary>
    public static string Display(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return string.Join('-', Enumerable.Range(0, (fingerprint.Length + 3) / 4)
            .Select(i => fingerprint.Substring(i * 4, Math.Min(4, fingerprint.Length - (i * 4)))));
    }
}
