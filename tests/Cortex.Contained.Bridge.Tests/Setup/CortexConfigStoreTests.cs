using Cortex.Contained.Bridge.Setup;

namespace Cortex.Contained.Bridge.Tests.Setup;

public sealed class CortexConfigStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(
        Path.GetTempPath(), "cortex-cfg-" + Guid.NewGuid().ToString("N"));

    public CortexConfigStoreTests() => Directory.CreateDirectory(this.dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.dir, true);
        }
        catch
        {
            // Best-effort.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WriteWithBackup_NoExistingFile_NoBackupCreated()
    {
        var path = Path.Combine(this.dir, "cortex.yml");
        var backup = CortexConfigStore.WriteWithBackup(path, "hello: world\n");

        Assert.Null(backup);
        Assert.Equal("hello: world\n", File.ReadAllText(path));
    }

    [Fact]
    public void WriteWithBackup_WithExistingFile_CreatesTimestampedBackupOfPreviousContent()
    {
        var path = Path.Combine(this.dir, "cortex.yml");
        File.WriteAllText(path, "first: 1\n");

        var backup = CortexConfigStore.WriteWithBackup(path, "second: 2\n");

        Assert.NotNull(backup);
        Assert.StartsWith(Path.Combine(this.dir, "cortex.yml.bak-"), backup, StringComparison.Ordinal);
        Assert.Equal("first: 1\n", File.ReadAllText(backup!));
        Assert.Equal("second: 2\n", File.ReadAllText(path));
    }

    [Fact]
    public void PruneOldBackups_KeepsMaxBackups_RestPruned()
    {
        var path = Path.Combine(this.dir, "cortex.yml");
        File.WriteAllText(path, "x: 1\n");

        // Create MaxBackups + 5 mock backup files with strictly ordered names.
        // Date stamps don't have to be real, just lexically ordered to match
        // CortexConfigStore.BackupPathFor's format (yyyyMMddHHmmss).
        for (var i = 0; i < CortexConfigStore.MaxBackups + 5; i++)
        {
            var stamp = $"2026010{i % 10:D1}010101{i:D2}"; // alphanumeric, ordered
            File.WriteAllText(path + ".bak-" + stamp, $"backup {i}");
        }

        var deleted = CortexConfigStore.PruneOldBackups(path);

        Assert.Equal(5, deleted);
        var remaining = Directory.EnumerateFiles(this.dir, "cortex.yml.bak-*").ToArray();
        Assert.Equal(CortexConfigStore.MaxBackups, remaining.Length);
    }

    [Fact]
    public void WriteWithBackup_SameSecondCollision_AppendsCounterSuffix()
    {
        // Two saves in the same UTC second must not fail. The second one
        // appends a -N counter to disambiguate so neither backup is lost.
        var path = Path.Combine(this.dir, "cortex.yml");

        File.WriteAllText(path, "v1\n");
        var firstBackup = CortexConfigStore.WriteWithBackup(path, "v2\n");

        // Pre-occupy the next-second backup path so a same-second write would
        // collide. Easier: re-pre-create a file at the path BackupPathFor
        // would return for "now", then write.
        var collidingStamp = CortexConfigStore.BackupPathFor(path, DateTimeOffset.UtcNow);
        if (!File.Exists(collidingStamp))
        {
            File.WriteAllText(collidingStamp, "decoy");
        }

        var secondBackup = CortexConfigStore.WriteWithBackup(path, "v3\n");

        Assert.NotNull(firstBackup);
        Assert.NotNull(secondBackup);
        Assert.NotEqual(firstBackup, secondBackup);
        Assert.True(File.Exists(firstBackup!));
        Assert.True(File.Exists(secondBackup!));
        Assert.Equal("v3\n", File.ReadAllText(path));
    }

    [Fact]
    public void BackupPathFor_FormatsStampInUtc()
    {
        var at = new DateTimeOffset(2026, 5, 21, 7, 14, 14, TimeSpan.FromHours(2));
        var path = Path.Combine(this.dir, "cortex.yml");

        var bak = CortexConfigStore.BackupPathFor(path, at);

        Assert.EndsWith(".bak-20260521051414", bak, StringComparison.Ordinal); // 07:14 +02:00 → 05:14 UTC
    }
}
