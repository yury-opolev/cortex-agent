using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>What <see cref="EncryptedSqlite.Prepare"/> found.</summary>
public enum EncryptedOpenOutcome
{
    /// <summary>No file yet; it will be created encrypted.</summary>
    Created,

    /// <summary>Already encrypted with this key.</summary>
    Opened,

    /// <summary>A plaintext file was encrypted in place (D2 maintenance window: nothing else has it open).</summary>
    Migrated,
}

/// <summary>
/// SQLite3 Multiple Ciphers helpers (D4). The passphrase goes in the connection string
/// (<c>Password</c>), the same convention as the Bridge's encrypted stores. Fail-closed: a file
/// that opens neither with the key nor as plaintext is never recreated or overwritten.
/// </summary>
public static class EncryptedSqlite
{
    private const int NotADatabase = 26;

    /// <summary>
    /// Prepares <paramref name="path"/> for opening with <paramref name="password"/>: migrates a
    /// plaintext database in place (checkpoint, <c>VACUUM</c> under <c>secure_delete</c>, then
    /// <c>PRAGMA rekey</c>), and throws <see cref="StorageKeyMismatchException"/> for a file
    /// encrypted with another key. Call before any other connection opens the file.
    /// </summary>
    public static EncryptedOpenOutcome Prepare(string path, string password, Action<string>? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureHex(password);
        if (!File.Exists(path))
        {
            return EncryptedOpenOutcome.Created;
        }

        if (CanRead(path, password))
        {
            return EncryptedOpenOutcome.Opened;
        }

        if (!CanRead(path, null))
        {
            throw new StorageKeyMismatchException($"{Path.GetFileName(path)} cannot be opened with the provided storage key.");
        }

        using (var connection = Open(path, password: null))
        {
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, "PRAGMA journal_mode=DELETE;");
            Execute(connection, "PRAGMA secure_delete=ON;");
            Execute(connection, "VACUUM;");
            faultInjector?.Invoke("before-rekey");
            Execute(connection, $"PRAGMA rekey = '{password}';");
        }

        return EncryptedOpenOutcome.Migrated;
    }

    /// <summary>The version of the linked SQLite3 Multiple Ciphers engine (<c>sqlite3mc_version()</c>), e.g. <c>2.7.1</c>.</summary>
    public static Version EngineVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite3mc_version();";
        var text = Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+\.\d+\.\d+");
        return match.Success ? Version.Parse(match.Value) : new Version(0, 0, 0);
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

    private static SqliteConnection Open(string path, string? password)
    {
        var connection = new SqliteConnection(ConnectionString(path, password, SqliteOpenMode.ReadWrite, pooling: false));
        connection.Open();
        return connection;
    }

    private static bool CanRead(string path, string? password)
    {
        try
        {
            using var connection = Open(path, password);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == NotADatabase)
        {
            return false;
        }
    }

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
