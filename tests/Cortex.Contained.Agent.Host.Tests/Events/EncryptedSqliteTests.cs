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

    private bool AnyFileContains(string marker) =>
        Directory.EnumerateFiles(this.dir, "*", SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains(marker, StringComparison.Ordinal));

    private string[] FileNames() => Directory.EnumerateFiles(this.dir).Select(Path.GetFileName).Order().ToArray()!;

    private string ReadValue()
    {
        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(this.DbPath, KeyA, SqliteOpenMode.ReadWrite, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT x FROM t;";
        return (string)cmd.ExecuteScalar()!;
    }

    private string JournalMode()
    {
        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(this.DbPath, KeyA, SqliteOpenMode.ReadWrite, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Prepare_PlaintextFile_ExportsToSiblingLeavingOnlyTheEncryptedDatabase()
    {
        this.Write(null, "zqplain");

        Assert.Equal(EncryptedOpenOutcome.Migrated, EncryptedSqlite.Prepare(this.DbPath, KeyA));

        Assert.Equal(["x.db"], this.FileNames());
        Assert.Equal("zqplain", this.ReadValue());
        Assert.False(this.AnyFileContains("zqplain"));
    }

    [Fact]
    public void Prepare_PlaintextFile_NeverRekeysInPlace()
    {
        this.Write(null, "zqplain");
        var stages = new List<string>();

        EncryptedSqlite.Prepare(this.DbPath, KeyA, stages.Add);

        Assert.Equal(["before-export", "after-export", "after-verify", "after-plaintext-erase", "directory-sync", "after-swap"], stages);
    }

    [Theory]
    [InlineData("before-export")]
    [InlineData("after-export")]
    [InlineData("after-verify")]
    [InlineData("after-plaintext-erase")]
    [InlineData("after-swap")]
    public void Prepare_CrashAtAnyStepOfTheExport_NextPrepareOpensWithTheDataAndLeavesNoPlaintext(string stage)
    {
        this.Write(null, "zqplain");
        Assert.Throws<IOException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyA, s => { if (s == stage) { throw new IOException("power loss"); } }));
        SqliteConnection.ClearAllPools();

        Assert.NotEqual(EncryptedOpenOutcome.Created, EncryptedSqlite.Prepare(this.DbPath, KeyA));

        Assert.Equal("zqplain", this.ReadValue());
        Assert.Equal(["x.db"], this.FileNames());
        Assert.False(this.AnyFileContains("zqplain"));
    }

    [Theory]
    [InlineData("before-export")]
    [InlineData("after-export")]
    public void Prepare_CrashBeforeTheCopyIsVerified_KeepsTheOriginalPlaintextReadable(string stage)
    {
        this.Write(null, "zqplain");
        Assert.Throws<IOException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyA, s => { if (s == stage) { throw new IOException("power loss"); } }));
        SqliteConnection.ClearAllPools();

        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(this.DbPath, null, SqliteOpenMode.ReadWrite, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT x FROM t;";
        Assert.Equal("zqplain", cmd.ExecuteScalar());
    }

    [Fact]
    public void Prepare_TornExportLeftByACrash_DiscardedAndTheOriginalMigrated()
    {
        this.Write(null, "zqplain");
        File.WriteAllBytes(this.DbPath + ".encrypting", new byte[5000]);

        Assert.Equal(EncryptedOpenOutcome.Migrated, EncryptedSqlite.Prepare(this.DbPath, KeyA));

        Assert.Equal("zqplain", this.ReadValue());
        Assert.Equal(["x.db"], this.FileNames());
    }

    [Fact]
    public void Prepare_ExportThatFailsVerification_KeepsTheOriginalAndThrows()
    {
        this.Write(null, "zqplain");

        Assert.Throws<InvalidDataException>(() => EncryptedSqlite.Prepare(
            this.DbPath,
            KeyA,
            s =>
            {
                if (s == "after-export")
                {
                    using var stream = new FileStream(this.DbPath + ".encrypting", FileMode.Open, FileAccess.Write);
                    stream.SetLength(stream.Length / 2);
                }
            }));
        SqliteConnection.ClearAllPools();

        Assert.Equal(["x.db"], this.FileNames());
        Assert.True(this.AnyFileContains("zqplain"));
    }

    [Fact]
    public void Prepare_VerifiedCopyButNoKey_KeepsEverythingAndFailsClosed()
    {
        this.Write(null, "zqplain");
        Assert.Throws<IOException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyA, s => { if (s == "after-plaintext-erase") { throw new IOException("power loss"); } }));
        SqliteConnection.ClearAllPools();
        var names = this.FileNames();

        Assert.Throws<StorageKeyMismatchException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyB));

        Assert.Equal(names, this.FileNames());
    }

    [Fact]
    public void Prepare_WrongKeyOnEncryptedFileWithAJournal_LeavesTheJournalUntouched()
    {
        this.Write(KeyA, "x");
        var journal = this.DbPath + "-journal";
        byte[] bytes = [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7, 1, 2, 3, 4];
        File.WriteAllBytes(journal, bytes);

        Assert.Throws<StorageKeyMismatchException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyB));

        Assert.Equal(bytes, File.ReadAllBytes(journal));
    }

    [Fact]
    public void Prepare_ProbeFailsForAReasonOtherThanNotADatabase_FailsClosedWithoutModifyingFiles()
    {
        this.Write(null, "zqplain");
        var before = File.ReadAllBytes(this.DbPath);
        using var locker = new SqliteConnection(EncryptedSqlite.ConnectionString(this.DbPath, null, SqliteOpenMode.ReadWrite, pooling: false));
        locker.Open();
        using (var begin = locker.CreateCommand())
        {
            begin.CommandText = "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }

        Assert.Throws<StorageLockedException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyA));

        Assert.Equal(["x.db"], this.FileNames());
        locker.Dispose();
        Assert.Equal(before, File.ReadAllBytes(this.DbPath));
    }

    [Fact]
    public void Prepare_EncryptedFile_IsSwitchedToWriteAheadLogging()
    {
        this.Write(KeyA, "zq");

        Assert.Equal(EncryptedOpenOutcome.Opened, EncryptedSqlite.Prepare(this.DbPath, KeyA));

        Assert.Equal("wal", this.JournalMode());
    }

    [Fact]
    public void Prepare_MigratedFile_IsInWriteAheadLoggingMode()
    {
        this.Write(null, "zqplain");

        EncryptedSqlite.Prepare(this.DbPath, KeyA);

        Assert.Equal("wal", this.JournalMode());
    }

    // A rollback journal that is really hot: a write transaction cut off mid-way, its db and journal copied while it was open.
    private void CopyOfACutOffWrite(string key)
    {
        var sourceDir = Path.Combine(this.dir, "src");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "src.db");
        using var writer = new SqliteConnection(EncryptedSqlite.ConnectionString(source, key, SqliteOpenMode.ReadWriteCreate, pooling: false));
        writer.Open();
        using var cmd = writer.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA cache_size=10; CREATE TABLE t (x TEXT); INSERT INTO t VALUES ('before'); CREATE TABLE big (b BLOB);";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 3000) INSERT INTO big SELECT randomblob(900) FROM n;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "BEGIN; UPDATE big SET b = randomblob(900); UPDATE t SET x = 'half-written';";
        cmd.ExecuteNonQuery();
        Assert.True(File.Exists(source + "-journal"));
        CopyShared(source, this.DbPath);
        CopyShared(source + "-journal", this.DbPath + "-journal");
    }

    private static void CopyShared(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    [Fact]
    public void Prepare_HotJournalAndTheRightKey_RollsBackAndOpens()
    {
        this.CopyOfACutOffWrite(KeyA);

        Assert.Equal(EncryptedOpenOutcome.Opened, EncryptedSqlite.Prepare(this.DbPath, KeyA));

        Assert.Equal("before", this.ReadValue());
    }

    [Fact]
    public void Prepare_HotJournalAndAWrongKey_IsAMismatchAndNothingIsTouched()
    {
        this.CopyOfACutOffWrite(KeyA);
        var db = File.ReadAllBytes(this.DbPath);
        var journal = File.ReadAllBytes(this.DbPath + "-journal");

        Assert.Throws<StorageKeyMismatchException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyB));

        Assert.Equal(db, File.ReadAllBytes(this.DbPath));
        Assert.Equal(journal, File.ReadAllBytes(this.DbPath + "-journal"));
        Assert.Empty(Directory.GetFiles(this.dir, "*.probe*"));
    }

    [Fact]
    public void Prepare_RepeatedProbesOfTheSameFileAndKey_CopyTheDatabaseOnlyOnce()
    {
        this.CopyOfACutOffWrite(KeyA);
        var copies = 0;
        void Count(string stage) { if (stage == "probe-copy") { copies++; } }

        Assert.Throws<StorageKeyMismatchException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyB, Count));
        Assert.Throws<StorageKeyMismatchException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyB, Count));
        Assert.Equal(1, copies);

        Assert.Equal(EncryptedOpenOutcome.Opened, EncryptedSqlite.Prepare(this.DbPath, KeyA, Count));
        Assert.Equal(2, copies);
    }

    // A wrong freelist count in the header: integrity_check complains, the rows stay readable and VACUUM rebuilds a clean file.
    private void WritePlaintextWithACorruptIndex()
    {
        using (var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(this.DbPath, null, SqliteOpenMode.ReadWriteCreate, pooling: false)))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (x TEXT); INSERT INTO t VALUES ('zqplain');";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(this.DbPath);
        bytes[39] = 7;
        File.WriteAllBytes(this.DbPath, bytes);

        using var check = new SqliteConnection(EncryptedSqlite.ConnectionString(this.DbPath, null, SqliteOpenMode.ReadWrite, pooling: false));
        check.Open();
        using var verify = check.CreateCommand();
        verify.CommandText = "PRAGMA integrity_check;";
        Assert.NotEqual("ok", verify.ExecuteScalar());
    }
    [Fact]
    public void Prepare_SourceFailsItsOwnIntegrityCheckButTheCopyIsNoWorse_Migrates()
    {
        this.WritePlaintextWithACorruptIndex();

        Assert.Equal(EncryptedOpenOutcome.Migrated, EncryptedSqlite.Prepare(this.DbPath, KeyA));

        Assert.Equal("zqplain", this.ReadValue());
        Assert.Equal(["x.db"], this.FileNames());
    }

    private static void TruncateTheCopy(string path, string stage)
    {
        if (stage == "after-export")
        {
            using var stream = new FileStream(path + ".encrypting", FileMode.Open, FileAccess.Write);
            stream.SetLength(stream.Length / 2);
        }
    }

    [Fact]
    public void Prepare_SourceNeedsRepairAndTheCopyIsWorse_ThrowsTheDistinctExceptionAndKeepsTheOriginal()
    {
        this.WritePlaintextWithACorruptIndex();

        var ex = Assert.Throws<StorageSourceNeedsRepairException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyA, s => TruncateTheCopy(this.DbPath, s)));

        Assert.Contains("repair", ex.Message, StringComparison.OrdinalIgnoreCase);
        SqliteConnection.ClearAllPools();
        Assert.Equal(["x.db"], this.FileNames());
        Assert.True(this.AnyFileContains("zqplain"));
    }

    [Fact]
    public void Prepare_AfterAFailedVerification_DoesNotRedoTheExportUntilTheBackoffIsReset()
    {
        this.Write(null, "zqplain");
        Assert.Throws<InvalidDataException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyA, s => TruncateTheCopy(this.DbPath, s)));
        Assert.NotNull(EncryptedSqlite.ExportProblem(this.DbPath));
        var stages = new List<string>();

        Assert.Throws<InvalidDataException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyA, stages.Add));
        Assert.Empty(stages);

        EncryptedSqlite.ResetExportBackoff(this.DbPath);
        Assert.Null(EncryptedSqlite.ExportProblem(this.DbPath));
        Assert.Equal(EncryptedOpenOutcome.Migrated, EncryptedSqlite.Prepare(this.DbPath, KeyA));
        Assert.Equal("zqplain", this.ReadValue());
    }

    [Fact]
    public void Prepare_AfterAFailedVerificationAndARepairOfTheSource_TriesAgain()
    {
        this.Write(null, "zqplain");
        Assert.Throws<InvalidDataException>(() => EncryptedSqlite.Prepare(this.DbPath, KeyA, s => TruncateTheCopy(this.DbPath, s)));
        this.Write(null, "zqmore");

        Assert.Equal(EncryptedOpenOutcome.Migrated, EncryptedSqlite.Prepare(this.DbPath, KeyA));
    }

    [Fact]
    public void DirectorySync_NeverThrows_AndFlushesOnUnix()
    {
        Assert.Equal(!OperatingSystem.IsWindows(), DirectorySync.TryFlush(this.dir));
        Assert.False(DirectorySync.TryFlush(Path.Combine(this.dir, "missing")));
    }
}
