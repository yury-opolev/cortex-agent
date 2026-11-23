using System.Text;
using Cortex.Contained.Agent.Host.Storage;
using MemoryMcp.Core.Security;

namespace Cortex.Contained.Agent.Host.Evaluation;

/// <summary>
/// How an evaluation command protects what it reads and copies (D4, D6). <see cref="Plaintext"/> on an
/// unencrypted install. <see cref="Encrypted"/> on an encrypted install, inside the agent process only (the
/// storage key never leaves it): the live stores are read with their own passphrases, every copy (run
/// directories, frozen corpora) is encrypted with keys of the ring derived for <see cref="Purpose"/>, and the
/// owner-authored files that carry text (datasets, query seeds, the sanitiser dictionary) are sealed with
/// AES-256-GCM. Reports, verdicts and manifests hold ids and numbers only and stay plaintext.
/// </summary>
public sealed class EvalProtection
{
    /// <summary>Purpose of the evaluation key ring (HKDF from the tenant key).</summary>
    public const string Purpose = "eval";

    /// <summary>Purpose of the key that seals workspace text files (derived from the evaluation ring).</summary>
    public const string SealedFilesPurpose = "eval-files";

    // The copy ring already holds its key, so this wait never elapses.
    private static readonly TimeSpan CopyKeyWait = TimeSpan.FromSeconds(5);

    private readonly StorageKeyRing? source;

    private EvalProtection(StorageKeyRing? source, StorageKeyRing? ring)
    {
        this.source = source;
        this.Ring = ring;
    }

    /// <summary>Nothing is encrypted (an install without encryption at rest).</summary>
    public static EvalProtection Plaintext { get; } = new(null, null);

    /// <summary>The ring the copies are encrypted with, or null in plaintext mode.</summary>
    public StorageKeyRing? Ring { get; }

    /// <summary>True when copies and sealed files are encrypted.</summary>
    public bool IsEncrypted => this.Ring is not null;

    /// <summary>Encryption of a <see cref="MessageStore"/> opened on a copy, or null when plaintext.</summary>
    public MessageStoreEncryption? CopyMessageEncryption => this.Ring is null ? null : new MessageStoreEncryption(this.Ring, CopyKeyWait);

    /// <summary>Protection on an encrypted install: <paramref name="live"/> must hold the tenant key.</summary>
    public static EvalProtection Encrypted(StorageKeyRing live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return new EvalProtection(live, live.Derive(Purpose));
    }

    /// <summary>True when <paramref name="path"/> exists and is sealed.</summary>
    public static bool IsSealed(string path) => File.Exists(path) && StorageFileCipher.IsEncryptedFile(path);

    /// <summary>
    /// The same protection reading a frozen corpus instead of the live stores: the source is itself an
    /// evaluation copy, encrypted with the evaluation ring.
    /// </summary>
    public EvalProtection ForFrozenSource() => this.Ring is null ? this : new EvalProtection(this.Ring, this.Ring);

    /// <summary>Passphrase of the source database for <paramref name="purpose"/> (e.g. <c>messages.db</c>), or null when plaintext.</summary>
    public string? SourcePassword(string purpose) => this.source?.PasswordFor(purpose);

    /// <summary>Passphrase of a copy for <paramref name="purpose"/>, or null when plaintext.</summary>
    public string? CopyPassword(string purpose) => this.Ring?.PasswordFor(purpose);

    /// <summary>Cipher of the source's memory content files.</summary>
    public IContentEncryptor SourceContent() =>
        this.source is null ? new NullContentEncryptor() : new StorageFileCipher(this.source.KeyFor(EncryptedMemoryStore.ContentPurpose), EncryptedMemoryStore.ContentPurpose);

    /// <summary>Cipher of a copy's memory content files.</summary>
    public IContentEncryptor CopyContent() =>
        this.Ring is null ? new NullContentEncryptor() : new StorageFileCipher(this.Ring.KeyFor(EncryptedMemoryStore.ContentPurpose), EncryptedMemoryStore.ContentPurpose);

    /// <summary>
    /// Reads a text file that may carry personal text. Encrypted mode accepts only sealed files (fail closed: a
    /// plaintext dataset on an encrypted install is refused; upload it through the Bridge instead). Plaintext mode
    /// refuses sealed files (they belong to an encrypted install).
    /// </summary>
    public string ReadText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        var sealedFile = StorageFileCipher.IsEncrypted(bytes);
        if (this.Ring is null)
        {
            return sealedFile
                ? throw new InvalidOperationException($"{Path.GetFileName(path)} is sealed with the storage key: run the command through the Bridge (scripts/Invoke-CortexEval.ps1).")
                : Encoding.UTF8.GetString(bytes);
        }

        if (!sealedFile)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} is plaintext on an encrypted install: upload it through the Bridge (scripts/Edit-CortexEvalFile.ps1) so it is sealed, then delete the plaintext copy.");
        }

        return Encoding.UTF8.GetString(this.FileCipher(path).Decrypt(bytes));
    }

    /// <summary>Non-blank lines of <see cref="ReadText"/>; an absent file has none.</summary>
    public IReadOnlyList<string> ReadLines(string path) =>
        File.Exists(path)
            ? this.ReadText(path).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
            : [];

    /// <summary>Writes <paramref name="text"/> atomically, sealed in encrypted mode.</summary>
    public void WriteText(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (this.Ring is not null)
        {
            bytes = this.FileCipher(path).Encrypt(bytes);
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Writes one line per element (see <see cref="WriteText"/>).</summary>
    public void WriteLines(string path, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        this.WriteText(path, string.Concat(lines.Select(l => l + "\n")));
    }

    // The file name is bound as associated data: a sealed dataset cannot be swapped for sealed seeds.
    private StorageFileCipher FileCipher(string path) =>
        new(this.Ring!.KeyFor(SealedFilesPurpose), SealedFilesPurpose + "/" + Path.GetFileName(path));
}
