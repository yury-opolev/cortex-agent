using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>Where an export package was written.</summary>
public sealed record ExportedPackage(string Path, string FileName, long Length, string Sha256);

/// <summary>
/// The off-machine form of a frozen version (D6): one passphrase-encrypted file (<see cref="PackageCipher"/>) holding
/// a tar archive of the sanitised files and the manifest. The sanitised <c>messages.db</c> travels encrypted with a
/// random key of its own (in the package manifest), so neither export nor import ever writes it in plaintext; the
/// version's pseudonym secret never leaves the machine. Import verifies every file against the manifest hashes,
/// re-seals the files for the local install and registers the sanitised datasets again.
/// </summary>
public static class FrozenCorpusPackage
{
    private const string PackageManifestEntry = "package.json";
    private const string DatabaseEntry = "messages.db";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Text files of a version, relative to its directory; sealed ones carry personal (sanitised) text.
    private static readonly (string Path, bool Sealed)[] TextFiles =
    [
        (FrozenCorpusBuilder.MemoriesFileName, true),
        ("history/dataset.jsonl", true), ("history/analysis-plan.md", false), ("history/ablation.json", false),
        ("memory/dataset.jsonl", true), ("memory/analysis-plan.md", false), ("memory/ablation.json", false),
    ];

    /// <summary>Writes <c>cortex-eval-corpus-&lt;version&gt;.cxpkg</c> into the packages directory and records the export in the manifest.</summary>
    public static ExportedPackage Export(FrozenCorpusStore store, string version, EvalProtection protection, string passphrase, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(time);
        var manifest = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
        if (manifest.Imported)
        {
            throw new InvalidOperationException($"{version} was imported from a package; export only versions frozen on this install.");
        }

        var directory = store.DirectoryOf(version);
        Directory.CreateDirectory(store.PackagesDirectory);
        var path = store.PackageOf(version);
        var temp = path + ".tmp";
        var databaseCopy = Path.Combine(store.PackagesDirectory, version + ".db.tmp");
        var databasePassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        try
        {
            DeleteFiles(databaseCopy, temp);
            EncryptedSqlite.CopyDatabase(Path.Combine(directory, "messages.db"), protection.CopyPassword(MessageStore.PasswordPurpose), databaseCopy, databasePassword);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var encrypted = PackageCipher.CreateWriter(file, passphrase))
            using (var tar = new TarWriter(encrypted, TarEntryFormat.Pax, leaveOpen: true))
            {
                var exported = manifest with { ExportedAt = null, PackageSha256 = null, Offsite = null, Encrypted = false };
                AddText(tar, PackageManifestEntry, JsonSerializer.Serialize(new PackageManifest(exported, databasePassword), Json));
                tar.WriteEntry(databaseCopy, DatabaseEntry);
                foreach (var (relative, sealedFile) in TextFiles)
                {
                    var source = Path.Combine(directory, relative);
                    if (File.Exists(source))
                    {
                        AddText(tar, relative, sealedFile ? protection.ReadText(source) : File.ReadAllText(source));
                    }
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFiles(databaseCopy, temp);
        }

        var info = new FileInfo(path);
        string sha256;
        using (var stream = info.OpenRead())
        {
            sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        (manifest with { ExportedAt = time.GetUtcNow(), PackageSha256 = sha256 }).Write(Path.Combine(directory, FrozenCorpusManifest.FileName));
        return new ExportedPackage(path, info.Name, info.Length, sha256);
    }

    /// <summary>
    /// Imports a package as a local version (same name), verifying every file against the manifest hashes.
    /// Throws <see cref="InvalidDataException"/> for a wrong passphrase or a damaged package and
    /// <see cref="InvalidOperationException"/> when the version already exists.
    /// </summary>
    public static FrozenCorpusManifest Import(FrozenCorpusStore store, string packagePath, EvalProtection protection, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protection);
        var staging = Path.Combine(store.Root, "import-" + Guid.NewGuid().ToString("N")[..8] + ".partial");
        Directory.CreateDirectory(staging);
        try
        {
            PackageManifest? package = null;
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            var databaseCopy = Path.Combine(staging, "messages.db.package");
            using (var file = File.OpenRead(packagePath))
            using (var decrypted = PackageCipher.OpenReader(file, passphrase))
            using (var tar = new TarReader(decrypted))
            {
                while (tar.GetNextEntry() is { } entry)
                {
                    if (entry.Name == DatabaseEntry && entry.DataStream is not null)
                    {
                        entry.ExtractToFile(databaseCopy, overwrite: false);
                    }
                    else if (entry.Name == PackageManifestEntry && entry.DataStream is not null)
                    {
                        package = JsonSerializer.Deserialize<PackageManifest>(entry.DataStream, Json);
                    }
                    else if (TextFiles.Any(t => t.Path == entry.Name))
                    {
                        // An empty file has no data stream.
                        using var reader = new StreamReader(entry.DataStream ?? Stream.Null, Encoding.UTF8);
                        texts[entry.Name] = reader.ReadToEnd();
                    }
                }
            }

            if (package?.Manifest is not { } manifest || !File.Exists(databaseCopy))
            {
                throw new InvalidDataException("The evaluation package is incomplete.");
            }

            var target = store.DirectoryOf(manifest.Version);
            if (Directory.Exists(target))
            {
                throw new InvalidOperationException($"{manifest.Version} already exists on this install.");
            }

            var databasePath = Path.Combine(staging, "messages.db");
            var copyPassword = protection.CopyPassword(MessageStore.PasswordPurpose);
            EncryptedSqlite.CopyDatabase(databaseCopy, package.DatabasePassword, databasePath, copyPassword);
            File.Delete(databaseCopy);
            Verify(manifest, FrozenCorpusBuilder.MessagesDigestKey, FrozenCorpusBuilder.MessagesDigest(databasePath, copyPassword));
            foreach (var (relative, sealedFile) in TextFiles)
            {
                if (!texts.TryGetValue(relative, out var text))
                {
                    continue;
                }

                Verify(manifest, relative, FrozenCorpusBuilder.TextDigest(text));
                var path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (sealedFile)
                {
                    protection.WriteText(path, text);
                }
                else
                {
                    File.WriteAllText(path, text);
                }
            }

            if (manifest.Files.Keys.FirstOrDefault(k => k != FrozenCorpusBuilder.MessagesDigestKey && !texts.ContainsKey(k)) is { } missing)
            {
                throw new InvalidDataException($"The evaluation package lacks {missing}.");
            }

            if (Directory.Exists(Path.Combine(staging, "history")))
            {
                new EvalWorkspace(Path.Combine(staging, "history"), protection).Register();
            }

            if (Directory.Exists(Path.Combine(staging, "memory")))
            {
                new MemoryEvalWorkspace(Path.Combine(staging, "memory"), protection).Register();
            }

            var imported = manifest with { Imported = true, Encrypted = protection.IsEncrypted, ExportedAt = null, PackageSha256 = null, Offsite = null };
            imported.Write(Path.Combine(staging, FrozenCorpusManifest.FileName));
            SqliteConnection.ClearAllPools();
            Directory.Move(staging, target);
            return imported;
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }

    private static void Verify(FrozenCorpusManifest manifest, string key, string digest)
    {
        if (!manifest.Files.TryGetValue(key, out var expected) || !string.Equals(expected, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{key} does not match the package manifest.");
        }
    }

    private static void AddText(TarWriter tar, string name, string text)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) };
        tar.WriteEntry(entry);
    }

    private static void DeleteFiles(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed record PackageManifest(FrozenCorpusManifest Manifest, string DatabasePassword);
}
