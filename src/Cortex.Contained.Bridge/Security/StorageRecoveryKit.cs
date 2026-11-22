using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Bridge.Security;

/// <summary>A storage key recovered from a kit.</summary>
/// <param name="TenantId">The tenant the kit was exported for.</param>
/// <param name="Base64Key">The 256-bit master key.</param>
/// <param name="Fingerprint">Its fingerprint (verified against the decrypted key).</param>
public sealed record RecoveredStorageKey(string TenantId, string Base64Key, string Fingerprint);

/// <summary>A recovery kit that cannot be opened (format, passphrase or integrity).</summary>
public sealed class StorageRecoveryKitException : Exception
{
    public StorageRecoveryKitException()
    {
    }

    public StorageRecoveryKitException(string message)
        : base(message)
    {
    }

    public StorageRecoveryKitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The owner's offline backup of a tenant storage key: the key encrypted with AES-256-GCM under a key
/// derived from a passphrase with PBKDF2-HMAC-SHA256 (600,000 iterations, 128-bit salt). Format, tenant and
/// key fingerprint are authenticated as associated data, so a kit cannot be relabelled for another tenant
/// or key. Without the passphrase the file is useless; the key is never logged.
/// </summary>
public static class StorageRecoveryKit
{
    /// <summary>Format identifier written into every kit.</summary>
    public const string Format = "cortex-storage-recovery-kit/1";

    /// <summary>PBKDF2 iterations of a new kit (OWASP 2023 guidance for PBKDF2-HMAC-SHA256).</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Fewest PBKDF2 iterations a kit may declare.</summary>
    public const int MinimumIterations = 100_000;

    /// <summary>Shortest accepted passphrase.</summary>
    public const int MinimumPassphraseLength = 12;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Creates the kit (UTF-8 JSON) for <paramref name="base64Key"/>.</summary>
    public static byte[] Create(string tenantId, string base64Key, string passphrase, DateTimeOffset createdAt, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(passphrase);
        if (passphrase.Length < MinimumPassphraseLength)
        {
            throw new ArgumentException($"The passphrase needs at least {MinimumPassphraseLength} characters.", nameof(passphrase));
        }

        var key = Convert.FromBase64String(base64Key);
        try
        {
            var fingerprint = StorageKeyFingerprint.Compute(key);
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ciphertext = new byte[key.Length];
            var tag = new byte[TagBytes];
            var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
            try
            {
                using var aes = new AesGcm(wrappingKey, TagBytes);
                aes.Encrypt(nonce, key, ciphertext, tag, AssociatedData(tenantId, fingerprint));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
            }

            var kit = new KitFile
            {
                Format = Format,
                TenantId = tenantId,
                KeyFingerprint = fingerprint,
                CreatedAt = createdAt,
                Kdf = new KdfSection { Name = "PBKDF2-HMAC-SHA256", Iterations = iterations, Salt = Convert.ToBase64String(salt) },
                Cipher = new CipherSection
                {
                    Name = "AES-256-GCM",
                    Nonce = Convert.ToBase64String(nonce),
                    Tag = Convert.ToBase64String(tag),
                    Ciphertext = Convert.ToBase64String(ciphertext),
                },
            };
            return JsonSerializer.SerializeToUtf8Bytes(kit, Json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens a kit; throws <see cref="StorageRecoveryKitException"/> for a wrong passphrase or a damaged file.</summary>
    public static RecoveredStorageKey Open(string kitJson, string passphrase, int minimumIterations = MinimumIterations)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        KitFile? kit;
        try
        {
            kit = JsonSerializer.Deserialize<KitFile>(kitJson, Json);
        }
        catch (JsonException ex)
        {
            throw new StorageRecoveryKitException("The file is not a recovery kit.", ex);
        }

        if (kit is not { Format: Format, TenantId.Length: > 0, KeyFingerprint.Length: > 0, Kdf: { } kdf, Cipher: { } cipher }
            || kdf.Iterations < minimumIterations
            || kdf.Iterations > 10_000_000)
        {
            throw new StorageRecoveryKitException("The file is not a supported recovery kit.");
        }

        byte[] salt, nonce, tag, ciphertext;
        try
        {
            salt = Convert.FromBase64String(kdf.Salt);
            nonce = Convert.FromBase64String(cipher.Nonce);
            tag = Convert.FromBase64String(cipher.Tag);
            ciphertext = Convert.FromBase64String(cipher.Ciphertext);
        }
        catch (FormatException ex)
        {
            throw new StorageRecoveryKitException("The recovery kit is damaged.", ex);
        }

        if (nonce.Length != NonceBytes || tag.Length != TagBytes || ciphertext.Length != StorageKeyFingerprint.KeyBytes)
        {
            throw new StorageRecoveryKitException("The recovery kit is damaged.");
        }

        var key = new byte[ciphertext.Length];
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, kdf.Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(wrappingKey, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, key, AssociatedData(kit.TenantId, kit.KeyFingerprint));
            if (!string.Equals(StorageKeyFingerprint.Compute(key), kit.KeyFingerprint, StringComparison.Ordinal))
            {
                throw new StorageRecoveryKitException("The recovery kit is damaged.");
            }

            return new RecoveredStorageKey(kit.TenantId, Convert.ToBase64String(key), kit.KeyFingerprint);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new StorageRecoveryKitException("Wrong passphrase, or the recovery kit was modified.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] AssociatedData(string tenantId, string fingerprint) =>
        Encoding.UTF8.GetBytes($"{Format}\n{tenantId}\n{fingerprint}");

    private sealed class KitFile
    {
        public string Format { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;

        public string KeyFingerprint { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public KdfSection? Kdf { get; set; }

        public CipherSection? Cipher { get; set; }
    }

    private sealed class KdfSection
    {
        public string Name { get; set; } = string.Empty;

        public int Iterations { get; set; }

        public string Salt { get; set; } = string.Empty;
    }

    private sealed class CipherSection
    {
        public string Name { get; set; } = string.Empty;

        public string Nonce { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public string Ciphertext { get; set; } = string.Empty;
    }
}
