using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>Where the off-machine copy of a version lives (D6): a file attached to the one SafeExchange secret the assistant may use.</summary>
public sealed record OffsiteCopy
{
    /// <summary>The only SafeExchange secret evaluation packages are attached to (it also holds the storage recovery kit).</summary>
    public const string SecretName = "cortex-recovery-kit";

    /// <summary>The secret (always <see cref="SecretName"/>).</summary>
    public string Secret { get; init; } = SecretName;

    /// <summary>The attachment's contentId, as reported by SafeExchange after the upload.</summary>
    public string? ContentId { get; init; }

    /// <summary>SHA-256 of the uploaded package (must equal the exported one).</summary>
    public string? Sha256 { get; init; }

    public DateTimeOffset UploadedAt { get; init; }
}

/// <summary>
/// <c>manifest.json</c> of a frozen corpus version (plaintext: ids, numbers and hashes only). The corpus range is
/// what D3 compares a purge with; <see cref="Files"/> holds the SHA-256 of every sanitised file's plaintext (the
/// version's registration hashes, verified again on import).
/// </summary>
public sealed record FrozenCorpusManifest
{
    /// <summary>File name inside the version directory.</summary>
    public const string FileName = "manifest.json";

    /// <summary>Format of the sanitiser that produced the version (bump when its rules change).</summary>
    public const int CurrentSanitiserVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary><c>v1</c>, <c>v2</c>, …</summary>
    public required string Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Oldest message in the corpus (timestamps are kept unchanged by the sanitiser).</summary>
    public DateTimeOffset CorpusFrom { get; init; }

    /// <summary>Newest message in the corpus.</summary>
    public DateTimeOffset CorpusTo { get; init; }

    public int Messages { get; init; }

    public int Memories { get; init; }

    public int HistoryQueries { get; init; }

    public int MemoryQueries { get; init; }

    public int SanitiserVersion { get; init; } = CurrentSanitiserVersion;

    /// <summary>Entries in the owner dictionary when the version was frozen.</summary>
    public int LexiconEntries { get; init; }

    /// <summary>Texts the leakage check scanned (it passed, or the version would not exist).</summary>
    public int LeakageTextsScanned { get; init; }

    /// <summary>True when the local copy is encrypted with the evaluation key (an encrypted install).</summary>
    public bool Encrypted { get; init; }

    /// <summary>True when the version came from an imported package (no local secret; it is not re-exported).</summary>
    public bool Imported { get; init; }

    /// <summary>Relative path → SHA-256 of the plaintext of every sanitised file.</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    /// <summary>The raw workspaces' registration hashes (<c>history/…</c>, <c>memory/…</c>) the sanitised datasets derive from.</summary>
    public IReadOnlyDictionary<string, string> RawRegistration { get; init; } = new Dictionary<string, string>();

    /// <summary>When the export package was last written, if ever.</summary>
    public DateTimeOffset? ExportedAt { get; init; }

    /// <summary>SHA-256 of the export package, if exported.</summary>
    public string? PackageSha256 { get; init; }

    /// <summary>The recorded off-machine copy, if the owner reported the upload.</summary>
    public OffsiteCopy? Offsite { get; init; }

    /// <summary>The package's file name, also its attachment name in SafeExchange.</summary>
    [JsonIgnore]
    public string PackageFileName => PackageFileNameOf(this.Version);

    /// <summary><c>cortex-eval-corpus-&lt;version&gt;.cxpkg</c>.</summary>
    public static string PackageFileNameOf(string version) => $"cortex-eval-corpus-{version}.cxpkg";

    /// <summary>The number of a version name (<c>v7</c> → 7), or null.</summary>
    public static int? NumberOf(string version) =>
        version.Length > 1 && version[0] == 'v' && int.TryParse(version.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    /// <summary>Reads a manifest, or null when absent or unreadable.</summary>
    public static FrozenCorpusManifest? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<FrozenCorpusManifest>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the manifest atomically.</summary>
    public void Write(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
