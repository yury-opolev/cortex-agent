using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Events;

[Trait("Category", "Integrity")]
public sealed class EncryptedSqliteTests : IDisposable
{
    private const string KeyA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string KeyB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"encsqlite-{Guid.NewGuid():N}");

    public EncryptedSqliteTests()
    {
        Directory.CreateDirectory(this.dir);
    }

    private string DbPath => Path.Combine(this.dir, "x.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string? password, string value)
    {
        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(this.DbPath, password, SqliteOpenMode.ReadWriteCreate, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS t (x TEXT); INSERT INTO t VALUES (@v);";
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void EngineVersion_IsPastTheRekeyDataLossFixes()
    {
        // SQLite3MC 2.7.0 fixed crash-during-rekey corruption (#291), keyless open deleting a hot journal (#292) and failed rekey (#294).
        Assert.True(EncryptedSqlite.EngineVersion() >= new Version(2, 7, 0), EncryptedSqlite.EngineVersion().ToString());
    }

    [Fact]
    public void Prepare_NoFile_Created()
    {
        Assert.Equal(EncryptedOpenOutcome.Created, EncryptedSqlite.Prepare(this.DbPath, KeyA));
        Assert.False(File.Exists(this.DbPath));
    }

    [Fact]
    public void Prepare_PlaintextFile_MigratedThenOpened()
    {
        this.Write(null, "zqplain");

        Assert.Equal(EncryptedOpenOutcome.Migrated, EncryptedSqlite.Prepare(this.DbPath, KeyA));
        Assert.Equal(EncryptedOpenOutcome.Opened, EncryptedSqlite.Prepare(this.DbPath, KeyA));
        Assert.DoesNotContain("zqplain", File.ReadAllText(this.DbPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_FileOfAnotherKey_ThrowsMismatch()
    {
        this.Write(KeyA, "x");

        Assert.Throws<StorageKeyMismatchException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyB));
    }

    [Fact]
    public void Prepare_NonHexPassword_Rejected()
    {
        Assert.Throws<ArgumentException>(() => EncryptedSqlite.Prepare(this.DbPath, "x'; DROP"));
    }
}
