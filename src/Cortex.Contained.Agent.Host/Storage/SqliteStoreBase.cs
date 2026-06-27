using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Base for the synchronous, single-connection SQLite stores. Owns the connection, optional
/// WAL journal mode, the <see cref="ExecuteNonQuery"/> helper, and <c>PRAGMA user_version</c>
/// schema-version accessors. Subclasses build their own schema (version-gated drop/recreate or
/// idempotent <c>CREATE TABLE IF NOT EXISTS</c>) in a method called from their constructor,
/// after the base constructor has opened the connection.
/// </summary>
public abstract class SqliteStoreBase : IDisposable
{
    private readonly SqliteConnection connection;
    private bool disposed;

    /// <param name="databasePath">Absolute path to the SQLite database file.</param>
    /// <param name="enableWalMode">When true, runs <c>PRAGMA journal_mode=WAL</c> after opening.</param>
    protected SqliteStoreBase(string databasePath, bool enableWalMode)
    {
        this.connection = new SqliteConnection($"Data Source={databasePath}");
        this.connection.Open();
        if (enableWalMode)
        {
            this.ExecuteNonQuery("PRAGMA journal_mode=WAL");
        }
    }

    /// <summary>The open SQLite connection. Subclasses use this for all commands.</summary>
    protected SqliteConnection Connection => this.connection;

    /// <summary>Combines <paramref name="root"/>/<paramref name="subdirectory"/>, ensures the directory exists, and returns the full path to <paramref name="fileName"/> within it.</summary>
    protected static string PrepareDatabasePath(string root, string subdirectory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var dir = Path.Combine(root, subdirectory);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    /// <summary>Executes a non-query SQL statement on the connection.</summary>
    protected void ExecuteNonQuery(string sql)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reads the SQLite <c>PRAGMA user_version</c>.</summary>
    protected int GetSchemaVersion()
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Sets the SQLite <c>PRAGMA user_version</c>.</summary>
    protected void SetSchemaVersion(int version)
    {
        this.ExecuteNonQuery(string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {version}"));
    }

    /// <summary>Disposes the connection. Override to release additional resources, then call <c>base.Dispose()</c>.</summary>
    public virtual void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
