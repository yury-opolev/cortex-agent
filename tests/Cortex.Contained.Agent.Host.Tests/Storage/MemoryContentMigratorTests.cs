using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>Encrypting memory content files in place: no plaintext left, crash-safe, fail closed.</summary>
[Trait("Category", "Integrity")]
public sealed class MemoryContentMigratorTests : IDisposable
{
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"memcontent-{Guid.NewGuid():N}");

    public MemoryContentMigratorTests()
    {
        Directory.CreateDirectory(this.dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static StorageFileCipher Cipher(byte[] key) => new(key, "memory-content");

    private string Write(string id, string text)
    {
        var path = Path.Combine(this.dir, id + ".memory.data");
        File.WriteAllText(path, text);
        return path;
    }

    private string Read(string id, byte[] key) =>
        Encoding.UTF8.GetString(Cipher(key).Decrypt(File.ReadAllBytes(Path.Combine(this.dir, id + ".memory.data"))));

    private bool AnyFileContains(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        return Directory.EnumerateFiles(this.dir).Any(f => File.ReadAllBytes(f).AsSpan().IndexOf(needle) >= 0);
    }

    private string HashDirectory() => string.Join(
        ",",
        Directory.EnumerateFiles(this.dir).Order(StringComparer.Ordinal).Select(f => Path.GetFileName(f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    [Fact]
    public void Migrate_PlaintextFiles_EncryptedAndNoPlaintextLeft()
    {
        this.Write("a", "zqmemmarker one");
        this.Write("b", "zqmemmarker two");

        var result = MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA));

        Assert.Equal(new MemoryContentMigrationResult(2, 0, 0), result);
        Assert.Equal("zqmemmarker one", this.Read("a", KeyA));
        Assert.False(this.AnyFileContains("zqmemmarker"));
        Assert.Empty(Directory.EnumerateFiles(this.dir, "*" + MemoryContentMigrator.MigratingSuffix));
        Assert.Equal(new MemoryContentMigrationResult(0, 2, 0), MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA)));
    }

    [Theory]
    [InlineData("after-encrypted-copy")]
    [InlineData("after-plaintext-overwrite")]
    public void Migrate_CrashMidFile_CompletedOnTheNextRun(string stage)
    {
        this.Write("a", "zqcrashmarker");

        Assert.Throws<IOException>(() => MemoryContentMigrator.Migrate(
            this.dir, Cipher(KeyA), s => { if (s == stage) { throw new IOException("power loss"); } }));

        MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA));

        Assert.Equal("zqcrashmarker", this.Read("a", KeyA));
        Assert.False(this.AnyFileContains("zqcrashmarker"));
        Assert.Empty(Directory.EnumerateFiles(this.dir, "*" + MemoryContentMigrator.MigratingSuffix));
    }

    [Fact]
    public void Migrate_TornEncryptedCopy_DroppedAndThePlaintextMigrated()
    {
        var path = this.Write("a", "zqtornmarker");
        File.WriteAllBytes(path + MemoryContentMigrator.MigratingSuffix, [.. StorageFileCipher.Magic, .. new byte[40]]);

        MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA));

        Assert.Equal("zqtornmarker", this.Read("a", KeyA));
        Assert.False(this.AnyFileContains("zqtornmarker"));
    }

    [Fact]
    public void Migrate_WrongKey_ThrowsAndChangesNothing()
    {
        this.Write("a", "x");
        MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA));
        this.Write("b", "zqlatermarker");
        var before = this.HashDirectory();

        Assert.Throws<StorageKeyMismatchException>(() => MemoryContentMigrator.Migrate(this.dir, Cipher(KeyB)));

        Assert.Equal(before, this.HashDirectory());
    }
}
