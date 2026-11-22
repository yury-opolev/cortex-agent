using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// The persisted decision to encrypt the agent's stores (<c>&lt;state&gt;/storage-encryption.json</c>), written
/// when the owner enables encryption from the Bridge. It lives next to the data, so the Launcher's compose
/// sync, image upgrades and Self-Update never lose it, and it is one-way: nothing in the product removes it.
/// </summary>
public sealed record StorageEncryptionMarker
{
    /// <summary>File name inside the state root.</summary>
    public const string FileName = "storage-encryption.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Format version.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Always true in a written marker.</summary>
    public bool EncryptAtRest { get; init; } = true;

    /// <summary>Fingerprint of the key the data is encrypted with (empty when unknown).</summary>
    public string KeyFingerprint { get; init; } = string.Empty;

    /// <summary>When encryption was enabled.</summary>
    public DateTimeOffset? EnabledAt { get; init; }

    /// <summary>
    /// Reads the marker. Missing file: null. A file that exists but cannot be read means encryption is on
    /// with an unknown fingerprint (fail closed: never fall back to plaintext).
    /// </summary>
    public static StorageEncryptionMarker? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<StorageEncryptionMarker>(File.ReadAllText(path), Json);
            return marker is { EncryptAtRest: true } ? marker : new StorageEncryptionMarker();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new StorageEncryptionMarker();
        }
    }

    /// <summary>Writes the marker atomically and durably (temp file, flush to disk, rename).</summary>
    public void Write(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
