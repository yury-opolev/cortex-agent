using System.Security.Cryptography;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Counts of one <see cref="MemoryContentMigrator.Migrate"/> run.</summary>
/// <param name="Migrated">Plaintext files encrypted in this run.</param>
/// <param name="AlreadyEncrypted">Files that were encrypted already.</param>
/// <param name="Recovered">Conversions interrupted by a crash and completed in this run.</param>
public sealed record MemoryContentMigrationResult(int Migrated, int AlreadyEncrypted, int Recovered);

/// <summary>
/// Encrypts the curated memory content files (<c>*.memory.data</c>) in place, crash-safe and without
/// leaving the plaintext behind (D4). Per file: the ciphertext is written to <c>&lt;file&gt;.migrating</c> and
/// flushed, the plaintext file is overwritten with zeros and flushed, then the ciphertext is renamed over it.
/// A crash at any point is completed on the next run: a <c>.migrating</c> file that authenticates replaces
/// the original; one that does not (torn write) is dropped while the original plaintext is still intact.
/// </summary>
public static class MemoryContentMigrator
{
    /// <summary>Suffix of an encrypted copy that has not replaced its plaintext file yet.</summary>
    public const string MigratingSuffix = ".migrating";

    private const string ContentPattern = "*.memory.data";

    /// <summary>
    /// Migrates <paramref name="memoriesDirectory"/>. Throws <see cref="StorageKeyMismatchException"/> before
    /// touching anything when an encrypted file does not authenticate with <paramref name="cipher"/>.
    /// </summary>
    /// <param name="memoriesDirectory">The content directory.</param>
    /// <param name="cipher">The memory-content cipher.</param>
    /// <param name="faultInjector">Test seam: called with <c>after-encrypted-copy</c> and <c>after-plaintext-overwrite</c>.</param>
    public static MemoryContentMigrationResult Migrate(string memoriesDirectory, StorageFileCipher cipher, Action<string>? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoriesDirectory);
        ArgumentNullException.ThrowIfNull(cipher);
        if (!Directory.Exists(memoriesDirectory))
        {
            return new MemoryContentMigrationResult(0, 0, 0);
        }

        var files = Directory.EnumerateFiles(memoriesDirectory, ContentPattern)
            .Where(f => f.EndsWith(".memory.data", StringComparison.Ordinal))
            .ToList();
        var encrypted = files.Where(StorageFileCipher.IsEncryptedFile).ToList();
        if (encrypted.Count > 0 && !Authenticates(cipher, File.ReadAllBytes(encrypted[0])))
        {
            throw new StorageKeyMismatchException("The memory content files cannot be decrypted with the provided storage key.");
        }

        var recovered = 0;
        foreach (var temp in Directory.EnumerateFiles(memoriesDirectory, ContentPattern + MigratingSuffix).ToList())
        {
            var target = temp[..^MigratingSuffix.Length];
            if (Authenticates(cipher, File.ReadAllBytes(temp)))
            {
                if (File.Exists(target) && !StorageFileCipher.IsEncryptedFile(target))
                {
                    var leftover = File.ReadAllBytes(target);
                    WriteDurably(target, new byte[leftover.Length]);
                }

                File.Move(temp, target, overwrite: true);
                recovered++;
                continue;
            }

            if (!File.Exists(target) || IsAllZero(File.ReadAllBytes(target)))
            {
                throw new InvalidDataException($"{Path.GetFileName(target)}: the interrupted encryption left no readable copy; it is kept for manual recovery.");
            }

            File.Delete(temp);
        }

        var migrated = 0;
        var already = 0;
        foreach (var file in Directory.EnumerateFiles(memoriesDirectory, ContentPattern)
                     .Where(f => f.EndsWith(".memory.data", StringComparison.Ordinal))
                     .ToList())
        {
            var bytes = File.ReadAllBytes(file);
            try
            {
                if (StorageFileCipher.IsEncrypted(bytes))
                {
                    already++;
                    continue;
                }

                var temp = file + MigratingSuffix;
                WriteDurably(temp, cipher.Encrypt(bytes));
                faultInjector?.Invoke("after-encrypted-copy");
                WriteDurably(file, new byte[bytes.Length]);
                faultInjector?.Invoke("after-plaintext-overwrite");
                File.Move(temp, file, overwrite: true);
                migrated++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return new MemoryContentMigrationResult(migrated, already - recovered, recovered);
    }

    private static bool Authenticates(StorageFileCipher cipher, byte[] blob)
    {
        try
        {
            CryptographicOperations.ZeroMemory(cipher.Decrypt(blob));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsAllZero(byte[] bytes) => !bytes.AsSpan().ContainsAnyExcept((byte)0);

    private static void WriteDurably(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(flushToDisk: true);
    }
}
