namespace Cortex.Contained.Contracts.Security;

/// <summary>What the first bytes of a stored file say about it.</summary>
public enum StoredFileState
{
    /// <summary>No such file.</summary>
    Absent,

    /// <summary>The file exists but is empty (no content either way).</summary>
    Empty,

    /// <summary>A readable plaintext file (a SQLite file starting with <c>SQLite format 3\0</c>).</summary>
    Plaintext,

    /// <summary>Anything else: an encrypted SQLite file has a random-looking first page.</summary>
    Encrypted,
}

/// <summary>
/// Classifies SQLite database files by their header without opening them (no key needed, nothing is
/// created). SQLite3 Multiple Ciphers encrypts page 1 including the header, so a plaintext database is
/// exactly one that starts with the SQLite magic string.
/// </summary>
public static class SqliteFileHeader
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    /// <summary>The state of the database file at <paramref name="path"/>.</summary>
    public static StoredFileState Classify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return StoredFileState.Absent;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read == 0)
        {
            return StoredFileState.Empty;
        }

        return read == header.Length && header.SequenceEqual(Magic) ? StoredFileState.Plaintext : StoredFileState.Encrypted;
    }
}
