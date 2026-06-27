using System.Text.Json;

namespace Cortex.Contained.Bridge.Storage;

/// <summary>
/// Base for small file-backed JSON settings stores: thread-safe read/write of a single
/// settings document of type <typeparamref name="T"/> at a fixed path. The document is
/// re-read from disk on every <see cref="Load"/> so external edits are picked up without a
/// restart; writes are write-through and create the parent directory on demand. Missing,
/// empty, or unreadable files (corrupt JSON or I/O errors) gracefully fall back to a fresh
/// <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The on-disk document type. Must be a reference type with a parameterless
/// constructor so a fresh default instance can be produced as the fallback value.</typeparam>
public abstract class JsonFileSettingsStore<T>
    where T : class, new()
{
    private readonly string filePath;
    private readonly Lock syncLock = new();

    /// <summary>JSON serializer options shared by all instances: indented output with
    /// case-insensitive property matching. Override <see cref="SerializerOptions"/> to change
    /// per-store.</summary>
    private static readonly JsonSerializerOptions DefaultSerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Initializes the store with the path of its backing JSON file.</summary>
    /// <param name="filePath">Absolute path of the JSON file backing this store.</param>
    protected JsonFileSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = filePath;
    }

    /// <summary>Path of the JSON file backing this store.</summary>
    protected string FilePath => this.filePath;

    /// <summary>Object used to serialize concurrent reads and writes. Derived stores that
    /// perform read-modify-write sequences hold this lock for the whole sequence.</summary>
    protected Lock SyncLock => this.syncLock;

    /// <summary>Serializer options used when reading and writing the document. Defaults to
    /// indented + case-insensitive; override to customize per store.</summary>
    protected virtual JsonSerializerOptions SerializerOptions => DefaultSerializerOptions;

    /// <summary>
    /// Read the document from disk, returning a fresh <typeparamref name="T"/> when the file is
    /// missing, empty, contains corrupt JSON, or cannot be read. Acquires <see cref="SyncLock"/>.
    /// </summary>
    protected T Load()
    {
        lock (this.syncLock)
        {
            return this.LoadInternal();
        }
    }

    /// <summary>
    /// Read the document from disk without acquiring <see cref="SyncLock"/>. Use only when the
    /// lock is already held (e.g. inside a read-modify-write sequence).
    /// </summary>
    protected T LoadInternal()
    {
        if (!File.Exists(this.filePath))
        {
            return new T();
        }

        try
        {
            var json = File.ReadAllText(this.filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new T();
            }

            return JsonSerializer.Deserialize<T>(json, this.SerializerOptions) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
        catch (IOException)
        {
            return new T();
        }
    }

    /// <summary>
    /// Serialize <paramref name="value"/> and write it through to disk, creating the parent
    /// directory if needed. Acquires <see cref="SyncLock"/>.
    /// </summary>
    protected void Save(T value)
    {
        lock (this.syncLock)
        {
            this.SaveInternal(value);
        }
    }

    /// <summary>
    /// Serialize <paramref name="value"/> and write it through to disk without acquiring
    /// <see cref="SyncLock"/>. Use only when the lock is already held.
    /// </summary>
    protected void SaveInternal(T value)
    {
        var dir = Path.GetDirectoryName(this.filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(value, this.SerializerOptions);
        File.WriteAllText(this.filePath, json);
    }
}
