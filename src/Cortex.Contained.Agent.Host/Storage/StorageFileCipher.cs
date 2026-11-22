using System.Security.Cryptography;
using System.Text;
using MemoryMcp.Core.Security;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Authenticated file encryption with a key derived from the tenant storage key (D4): AES-256-GCM with a
/// fresh random 96-bit nonce per write. Layout: <c>"CXE1"</c> magic (4 bytes), nonce (12), tag (16),
/// ciphertext. The associated data binds every blob to its purpose label, so a memory content file and a
/// session snapshot cannot be swapped for each other. Thread-safe.
/// </summary>
public sealed class StorageFileCipher : IContentEncryptor
{
    /// <summary>Bytes before the ciphertext.</summary>
    public const int HeaderSize = 4 + NonceSize + TagSize;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] key;
    private readonly byte[] associatedData;

    /// <param name="key">The 256-bit key for <paramref name="label"/> (<see cref="StorageKeyRing.KeyFor"/>).</param>
    /// <param name="label">The purpose the files belong to, e.g. <c>memory-content</c>.</param>
    public StorageFileCipher(byte[] key, string label)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (key.Length != StorageKeyRing.KeyBytes)
        {
            throw new ArgumentException($"The key must have {StorageKeyRing.KeyBytes} bytes.", nameof(key));
        }

        this.key = (byte[])key.Clone();
        this.associatedData = Encoding.UTF8.GetBytes("cortex-storage-file/1\n" + label);
    }

    /// <summary>The magic prefix of an encrypted file.</summary>
    public static ReadOnlySpan<byte> Magic => "CXE1"u8;

    /// <summary>
    /// True when <paramref name="content"/> starts with the encrypted-file magic and what follows is not readable text:
    /// the nonce and tag are random, whereas a plaintext note that merely begins with <c>CXE1</c> continues as text.
    /// </summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> content) =>
        content.Length >= HeaderSize && content.StartsWith(Magic) && !LooksLikeText(content[Magic.Length..HeaderSize]);

    // A random 28 bytes are almost never valid UTF-8 without control characters; the last bytes may cut a multi-byte character.
    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        for (var cut = 0; cut <= 3; cut++)
        {
            var candidate = bytes[..^cut];
            if (System.Text.Unicode.Utf8.IsValid(candidate) && !candidate.ContainsAnyInRange((byte)0, (byte)8) && !candidate.Contains((byte)0x7F)
                && !candidate.ContainsAnyInRange((byte)14, (byte)31))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the file at <paramref name="path"/> starts with the encrypted-file magic.</summary>
    public static bool IsEncryptedFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> prefix = stackalloc byte[HeaderSize];
        var read = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        return IsEncrypted(prefix[..read]);
    }

    /// <inheritdoc />
    public byte[] Encrypt(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var blob = new byte[HeaderSize + plaintext.Length];
        Magic.CopyTo(blob);
        var nonce = blob.AsSpan(4, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(this.key, TagSize);
        aes.Encrypt(nonce, plaintext, blob.AsSpan(HeaderSize), blob.AsSpan(4 + NonceSize, TagSize), this.associatedData);
        return blob;
    }

    /// <summary>
    /// Decrypts a blob written by <see cref="Encrypt"/>. A blob without the magic throws
    /// <see cref="InvalidDataException"/> (plaintext is never served as if it were authenticated); a wrong key
    /// or any modified byte throws <see cref="AuthenticationTagMismatchException"/>.
    /// </summary>
    public byte[] Decrypt(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!IsEncrypted(ciphertext))
        {
            throw new InvalidDataException("The file is not encrypted with the storage key.");
        }

        var plaintext = new byte[ciphertext.Length - HeaderSize];
        using var aes = new AesGcm(this.key, TagSize);
        aes.Decrypt(
            ciphertext.AsSpan(4, NonceSize),
            ciphertext.AsSpan(HeaderSize),
            ciphertext.AsSpan(4 + NonceSize, TagSize),
            plaintext,
            this.associatedData);
        return plaintext;
    }
}
