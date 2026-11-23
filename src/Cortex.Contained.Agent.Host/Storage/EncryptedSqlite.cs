using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cortex.Contained.Contracts.Security;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>What <see cref="EncryptedSqlite.Prepare"/> found.</summary>
public enum EncryptedOpenOutcome
{
    /// <summary>No file yet; it will be created encrypted.</summary>
    Created,

    /// <summary>Already encrypted with this key.</summary>
    Opened,

    /// <summary>A plaintext file was encrypted (D2 maintenance window: nothing else has it open).</summary>
    Migrated,
}

/// <summary>
/// SQLite3 Multiple Ciphers helpers (D4). The passphrase goes in the connection string
/// (<c>Password</c>), the same convention as the Bridge's encrypted stores. Fail-closed: a file
/// that opens neither with the key nor as plaintext is never recreated or overwritten.
/// </summary>
public static partial class EncryptedSqlite
{
    /// <summary>Suffix of the encrypted copy being written next to a plaintext database.</summary>
    public const string EncryptingSuffix = ".encrypting";

    /// <summary>Suffix of an encrypted copy that is complete and verified but has not replaced the plaintext yet.</summary>
    public const string EncryptedSuffix = ".encrypted";

    private const int NotADatabase = 26;
    private const int Corrupt = 11;
    private const int BusyTimeoutSeconds = 2;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExportFailure> ExportFailures = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ProbeKey, bool> ProbeResults = new();

    private sealed record ExportFailure((long Length, long Ticks) Stamp, bool NeedsRepair, string Message)
    {
        public Exception ToException() => this.NeedsRepair ? new StorageSourceNeedsRepairException(this.Message) : new InvalidDataException(this.Message);
    }

    private readonly record struct ProbeKey(string Path, (long Length, long Ticks) Database, (long Length, long Ticks) Journal, string KeyHash);

    /// <summary>
    /// Prepares <paramref name="path"/> for opening with <paramref name="password"/>. A plaintext database is
    /// never rekeyed in place: it is exported to <c>&lt;db&gt;.encrypting</c> (<c>VACUUM INTO</c> a keyed target),
    /// flushed, verified against the source (integrity check, row counts of every table), renamed to
    /// <c>&lt;db&gt;.encrypted</c>, and only then is the plaintext overwritten with zeros and the copy renamed over
    /// it. A crash at any step is resolved by the next call: an unverified copy is dropped while the plaintext is
    /// still intact, a verified one completes the swap. Throws <see cref="StorageKeyMismatchException"/> for a
    /// file encrypted with another key and <see cref="StorageLockedException"/> when the file cannot be probed
    /// (nothing is modified). A file is classified by its 16-byte header, so an encrypted database is never opened
    /// without its key. Call before any other connection opens the file.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="password">The hexadecimal passphrase.</param>
    /// <param name="faultInjector">Test seam: called with <c>before-export</c>, <c>after-export</c>, <c>after-verify</c>, <c>after-plaintext-erase</c> and <c>after-swap</c>.</param>
    public static EncryptedOpenOutcome Prepare(string path, string password, Action<string>? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureHex(password);

        if (CompleteInterruptedExport(path, password, faultInjector))
        {
            EnsureWriteAheadLog(path, password);
            return EncryptedOpenOutcome.Migrated;
        }

        switch (SqliteFileHeader.Classify(path))
        {
            case StoredFileState.Absent:
                return EncryptedOpenOutcome.Created;
            case StoredFileState.Empty:
                return EncryptedOpenOutcome.Opened;
            case StoredFileState.Encrypted:
                if (!(File.Exists(path + "-journal") ? CanReadOnCopy(path, password, faultInjector) : CanRead(path, password, corruptMeansWrongKey: false)))
                {
                    throw new StorageKeyMismatchException($"{Path.GetFileName(path)} cannot be opened with the provided storage key.");
                }

                EnsureWriteAheadLog(path, password);
                return EncryptedOpenOutcome.Opened;
            default:
                try
                {
                    ExportToSibling(path, password, faultInjector);
                }
                catch (SqliteException ex)
                {
                    DeleteWithSidecars(path + EncryptingSuffix);
                    throw ProbeFailed(path, ex);
                }

                EnsureWriteAheadLog(path, password);
                return EncryptedOpenOutcome.Migrated;
        }
    }

    /// <summary>The version of the linked SQLite3 Multiple Ciphers engine (<c>sqlite3mc_version()</c>), e.g. <c>2.7.1</c>.</summary>
    public static Version EngineVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite3mc_version();";
        var text = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        var match = VersionPattern().Match(text);
        return match.Success ? Version.Parse(match.Value) : new Version(0, 0, 0);
    }

    /// <summary>
    /// Writes a consistent copy of <paramref name="source"/> (read with <paramref name="sourcePassword"/>, null for
    /// plaintext; the database may be in use) to <paramref name="target"/>, encrypted with <paramref name="targetPassword"/>
    /// (null: plaintext). <c>VACUUM INTO</c> writes the target pages already encrypted, so no other form of the copy
    /// ever reaches the disk. Never overwrites: throws <see cref="IOException"/> when <paramref name="target"/> exists.
    /// </summary>
    public static void CopyDatabase(string source, string? sourcePassword, string target, string? targetPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (sourcePassword is not null)
        {
            EnsureHex(sourcePassword);
        }

        if (targetPassword is not null)
        {
            EnsureHex(targetPassword);
        }

        if (File.Exists(target))
        {
            throw new IOException($"{Path.GetFileName(target)} already exists.");
        }

        using var connection = new SqliteConnection(ConnectionString(source, sourcePassword, SqliteOpenMode.ReadOnly, pooling: false));
        connection.Open();

        // SQLite3 Multiple Ciphers gives a target without a key the source's key; an empty key makes it plaintext.
        var into = ExportTarget(target, targetPassword ?? string.Empty);
        Execute(connection, $"VACUUM main INTO '{into}';");
    }

    /// <summary>A connection string for <paramref name="path"/>, encrypted when <paramref name="password"/> is set.</summary>
    public static string ConnectionString(string path, string? password, SqliteOpenMode mode, bool pooling) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = pooling,
            Password = password,
        }.ToString();

    /// <summary>The message of an encryption that failed verification and is not retried until the file changes, the agent restarts or <see cref="ResetExportBackoff"/>; otherwise null.</summary>
    public static string? ExportProblem(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ExportFailures.TryGetValue(path, out var failure) && failure.Stamp == StampOf(path) ? failure.Message : null;
    }

    /// <summary>Allows the next <see cref="Prepare"/> to retry an export that failed verification.</summary>
    public static void ResetExportBackoff(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ExportFailures.TryRemove(path, out _);
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();

    private static void ExportToSibling(string path, string password, Action<string>? faultInjector)
    {
        var encrypting = path + EncryptingSuffix;
        var ready = path + EncryptedSuffix;

        if (ExportFailures.TryGetValue(path, out var earlier))
        {
            if (earlier.Stamp == StampOf(path))
            {
                throw earlier.ToException();
            }

            ExportFailures.TryRemove(path, out _);
        }

        faultInjector?.Invoke("before-export");
        DeleteWithSidecars(encrypting);

        bool? failedNeedsRepair = null;
        using (var source = Open(path, password: null))
        {
            try
            {
                var sourceProblems = IntegrityProblems(source);
                Execute(source, "PRAGMA wal_checkpoint(TRUNCATE);");
                Execute(source, $"VACUUM main INTO '{ExportTarget(encrypting, password)}';");
                var expected = TableCounts(source);
                FlushToDisk(encrypting);
                faultInjector?.Invoke("after-export");

                if (!VerifiesAgainst(encrypting, password, expected, sourceProblems.Count))
                {
                    failedNeedsRepair = sourceProblems.Count > 0;
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is Corrupt or NotADatabase)
            {
                failedNeedsRepair = true;
            }
        }

        if (failedNeedsRepair is { } needsRepair)
        {
            DeleteWithSidecars(encrypting);
            throw Fail(path, needsRepair);
        }

        File.Move(encrypting, ready);
        DirectorySync.TryFlush(Path.GetDirectoryName(Path.GetFullPath(path))!);
        faultInjector?.Invoke("after-verify");

        ExportFailures.TryRemove(path, out _);
        FinishSwap(path, ready, faultInjector);
    }

    // The stamp is taken after the source connection is closed (closing a WAL database rewrites the file).
    private static Exception Fail(string path, bool needsRepair)
    {
        var failure = new ExportFailure(
            StampOf(path),
            needsRepair,
            needsRepair
                ? $"{Path.GetFileName(path)} fails its own integrity check and its encrypted copy is worse: repair or restore the database, then restart the agent. It stays plaintext and locked until then."
                : $"The encrypted copy of {Path.GetFileName(path)} did not verify; the original is kept. It is not retried until the file changes or the agent restarts.");
        ExportFailures[path] = failure;
        return failure.ToException();
    }

    private static (long Length, long Ticks) StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, -1);
    }

    private static List<string> IntegrityProblems(SqliteConnection connection)
    {
        var problems = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var line = reader.GetString(0);
            if (!string.Equals(line, "ok", StringComparison.Ordinal))
            {
                problems.Add(line);
            }
        }

        return problems;
    }

    private static bool CompleteInterruptedExport(string path, string password, Action<string>? faultInjector)
    {
        var encrypting = path + EncryptingSuffix;
        var ready = path + EncryptedSuffix;

        if (File.Exists(encrypting))
        {
            DeleteWithSidecars(encrypting);
        }

        if (!File.Exists(ready))
        {
            return false;
        }

        if (VerifiesAlone(ready, password))
        {
            FinishSwap(path, ready, faultInjector);
            return true;
        }

        if (SqliteFileHeader.Classify(path) == StoredFileState.Plaintext)
        {
            DeleteWithSidecars(ready);
            return false;
        }

        throw new StorageKeyMismatchException($"{Path.GetFileName(ready)} (an interrupted encryption) cannot be opened with the provided storage key; it is kept untouched.");
    }

    private static void FinishSwap(string path, string ready, Action<string>? faultInjector)
    {
        if (File.Exists(path) && !IsPlaintextOrZeroed(path))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is neither plaintext nor erased while a verified encrypted copy exists; both are kept for manual recovery.");
        }

        SecureErase.File(path);
        foreach (var sidecar in new[] { "-journal", "-wal", "-shm" })
        {
            SecureErase.File(path + sidecar);
        }

        faultInjector?.Invoke("after-plaintext-erase");
        File.Move(ready, path);
        faultInjector?.Invoke("directory-sync");
        DirectorySync.TryFlush(Path.GetDirectoryName(Path.GetFullPath(path))!);
        faultInjector?.Invoke("after-swap");
    }

    private static bool IsPlaintextOrZeroed(string path)
    {
        if (SqliteFileHeader.Classify(path) is StoredFileState.Plaintext or StoredFileState.Empty)
        {
            return true;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        return !header[..read].ContainsAnyExcept((byte)0);
    }

    private static string ExportTarget(string target, string password)
    {
        var uriPath = Path.GetFullPath(target).Replace('\\', '/').Replace("%", "%25", StringComparison.Ordinal)
            .Replace("?", "%3f", StringComparison.Ordinal).Replace("#", "%23", StringComparison.Ordinal);
        return $"file:{uriPath}?key={password}".Replace("'", "''", StringComparison.Ordinal);
    }

    // The copy passes when every table has the source's row count and it is no more damaged than the source
    // (VACUUM rebuilds indexes, so a source whose integrity check fails can still yield a clean or equally flawed copy).
    private static bool VerifiesAgainst(string path, string password, Dictionary<string, long> expected, int sourceProblemCount)
    {
        try
        {
            using var connection = Open(path, password);
            var copyProblems = IntegrityProblems(connection).Count;
            return (copyProblems == 0 || (sourceProblemCount > 0 && copyProblems <= sourceProblemCount))
                && TableCounts(connection).OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(p => p.Key, StringComparer.Ordinal));
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // A copy that was verified before it was renamed only has to open with the key again.
    private static bool VerifiesAlone(string path, string password)
    {
        try
        {
            using var connection = Open(path, password);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is NotADatabase or Corrupt)
        {
            return false;
        }
        catch (SqliteException ex)
        {
            throw ProbeFailed(path, ex);
        }
    }

    private static Dictionary<string, long> TableCounts(SqliteConnection connection)
    {
        // Virtual tables need their module (vec0 is not loaded here); their shadow tables are ordinary tables and are counted.
        var names = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite!_%' ESCAPE '!' AND sql NOT LIKE 'CREATE VIRTUAL TABLE%';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\" NOT INDEXED;";
            counts[name] = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    private static void EnsureWriteAheadLog(string path, string password)
    {
        using var connection = Open(path, password);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        if (!string.Equals(Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
        {
            Execute(connection, "PRAGMA journal_mode=WAL;");
        }
    }

    private static void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteWithSidecars(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-journal", "-wal", "-shm" })
        {
            if (File.Exists(path + suffix))
            {
                File.Delete(path + suffix);
            }
        }
    }

    private static SqliteConnection Open(string path, string? password)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            Password = password,
            DefaultTimeout = BusyTimeoutSeconds,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static bool CanRead(string path, string password, bool corruptMeansWrongKey)
    {
        try
        {
            using var connection = Open(path, password);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == NotADatabase || (corruptMeansWrongKey && ex.SqliteErrorCode == Corrupt))
        {
            return false;
        }
        catch (SqliteException ex)
        {
            throw ProbeFailed(path, ex);
        }
    }

    // A rollback journal next to an encrypted file is replayed by the first open, with whatever key that open uses;
    // a wrong key could write garbage pages back. The key is therefore tried on a copy first. On the copy, SQLite3MC
    // answers a wrong key plus a hot journal with "corrupt" rather than "not a database", so both mean the key does
    // not open it. The outcome is remembered per file state and key, so a locked store does not copy the database again.
    private static bool CanReadOnCopy(string path, string password, Action<string>? faultInjector)
    {
        var journal = path + "-journal";
        var cacheKey = new ProbeKey(path, StampOf(path), StampOf(journal), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(password))));
        if (ProbeResults.TryGetValue(cacheKey, out var known))
        {
            return known;
        }

        var probe = path + ".probe";
        try
        {
            faultInjector?.Invoke("probe-copy");
            DeleteWithSidecars(probe);
            File.Copy(path, probe, overwrite: true);
            File.Copy(journal, probe + "-journal", overwrite: true);
            var opens = CanRead(probe, password, corruptMeansWrongKey: true);
            if (ProbeResults.Count > 64)
            {
                ProbeResults.Clear();
            }

            ProbeResults[cacheKey] = opens;
            return opens;
        }
        finally
        {
            DeleteWithSidecars(probe);
        }
    }

    private static StorageLockedException ProbeFailed(string path, SqliteException ex) =>
        new($"{Path.GetFileName(path)} could not be probed or converted (SQLite error {ex.SqliteErrorCode}); it was not modified and stays locked.", ex);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureHex(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (!password.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The storage passphrase must be hexadecimal.", nameof(password));
        }
    }
}

/// <summary>A database is encrypted with a different key than the one provided (fail closed).</summary>
public sealed class StorageKeyMismatchException : Exception
{
    public StorageKeyMismatchException()
    {
    }

    public StorageKeyMismatchException(string message)
        : base(message)
    {
    }

    public StorageKeyMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The plaintext database fails its own integrity check and its encrypted copy is worse: repair it, then retry.</summary>
public sealed class StorageSourceNeedsRepairException : IOException
{
    public StorageSourceNeedsRepairException()
    {
    }

    public StorageSourceNeedsRepairException(string message)
        : base(message)
    {
    }

    public StorageSourceNeedsRepairException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
