using System.Security.Cryptography;
using System.Text;

namespace Cortex.Contained.Bridge.Connectors.Security;

/// <summary>Cryptographic utilities for connector token and pairing-code generation.</summary>
public static class ConnectorTokenGenerator
{
    private const string PairingAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>
    /// Generates a 32-byte cryptographically random token encoded as Base64-URL (no padding).
    /// </summary>
    public static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Generates a human-transcribable pairing code in the shape <c>XXXX-XXX</c> drawn from
    /// an unambiguous alphabet that excludes <c>0</c>, <c>1</c>, <c>I</c>, and <c>O</c>.
    /// Uses <see cref="RandomNumberGenerator.GetInt32(int, int)"/> — not <see cref="Random"/> and
    /// not modulo-biased byte sampling.
    /// </summary>
    public static string CreatePairingCode()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < 4; i++)
        {
            chars[i] = PairingAlphabet[RandomNumberGenerator.GetInt32(0, PairingAlphabet.Length)];
        }

        chars[4] = '-';

        for (var i = 5; i < 8; i++)
        {
            chars[i] = PairingAlphabet[RandomNumberGenerator.GetInt32(0, PairingAlphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>
    /// Compares two tokens in constant time by hashing each to SHA-256 first,
    /// so that unequal lengths do not leak via an early return.
    /// Returns false (without throwing) when either argument is null.
    /// </summary>
    public static bool TokensEqual(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        var hashA = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var hashB = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
}
