using System.Security.Cryptography;

namespace Cortex.Contained.Contracts.Security;

/// <summary>
/// The passphrase key derivation shared by every passphrase-encrypted file the owner keeps off the machine (the
/// storage recovery kit, evaluation corpus packages): PBKDF2-HMAC-SHA256 with a 128-bit random salt and 600,000
/// iterations (OWASP 2023 guidance) into a 256-bit AES key.
/// </summary>
public static class PassphraseKdf
{
    /// <summary>Name written into file headers.</summary>
    public const string Name = "PBKDF2-HMAC-SHA256";

    /// <summary>Iterations of a new file.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Fewest iterations a file may declare.</summary>
    public const int MinimumIterations = 100_000;

    /// <summary>Most iterations a file may declare (a hostile header cannot pin the CPU).</summary>
    public const int MaximumIterations = 10_000_000;

    /// <summary>Shortest accepted passphrase.</summary>
    public const int MinimumPassphraseLength = 12;

    /// <summary>Salt length.</summary>
    public const int SaltBytes = 16;

    /// <summary>The 256-bit key for <paramref name="passphrase"/>; the caller zeroes it after use.</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(salt);
        return Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
    }
}
