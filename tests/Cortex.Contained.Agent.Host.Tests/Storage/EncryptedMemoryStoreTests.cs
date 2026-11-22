using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>
/// The curated memory store under the tenant key: memory.db (chunks, sqlite-vec vectors, FTS5 keyword
/// index) through SQLite3 Multiple Ciphers and the content files through AES-GCM.
/// </summary>
[Trait("Category", "Integrity")]
public sealed class EncryptedMemoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"memstore-enc-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions settings;

    public EncryptedMemoryStoreTests()
    {
        this.settings = new MemoryMcpOptions
        {
            DataDirectory = this.dir,
            DatabaseFileName = "memory.db",
            Ollama = new OllamaOptions { Dimensions = 4 },
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static StorageKeyRing Ring(byte seed = 1)
    {
        var ring = new StorageKeyRing();
        ring.Provide(Convert.ToBase64String(Enumerable.Range(seed, 32).Select(i => (byte)i).ToArray()));
        return ring;
    }

    private static List<ChunkRecord> Chunk(string memoryId, string? title) =>
    [
        new ChunkRecord
        {
            MemoryId = memoryId,
            ChunkIndex = 0,
            StartOffset = 0,
            Length = 10,
            Title = title,
            Tags = ["zqtagmarker"],
            CreatedAt = T0,
            UpdatedAt = T0,
        },
    ];

    private static List<float[]> Vector(float x) => [[x, 1f, 0f, 0f]];

    private EncryptedMemoryStore Encrypted(StorageKeyRing ring, TimeSpan? wait = null) =>
        new(Options.Create(this.settings), ring, wait ?? TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);

    private async Task SeedPlaintextAsync()
    {
        using (var plain = new SqliteVecMemoryStore(Options.Create(this.settings), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance))
        {
            await plain.InitializeAsync();
            await plain.StoreMemoryAsync("m1", "zqkeptmarker alpha", Chunk("m1", "zqtitlemarker"), Vector(1));
            await plain.StoreMemoryAsync("m2", "zqdeletedmarker beta", Chunk("m2", null), Vector(2));
            await plain.DeleteMemoryAsync("m2");
        }

        SqliteConnection.ClearAllPools();
    }

    private bool AnyFileContains(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        foreach (var file in Directory.EnumerateFiles(this.dir, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (copy.ToArray().AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private string HashAllFiles() => string.Join(
        ",",
        Directory.EnumerateFiles(this.dir, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    [Fact]
    public async Task PlaintextStore_MigratedOnOpen_ServesSearchAndLeavesNoPlaintextAnywhere()
    {
        await this.SeedPlaintextAsync();
        Assert.True(this.AnyFileContains("zqkeptmarker"));

        using (var store = this.Encrypted(Ring()))
        {
            Assert.Equal("zqkeptmarker alpha", (await store.GetMemoryAsync("m1"))!.Content);
            Assert.Equal("m1", Assert.Single(await store.SearchDenseAsync([1f, 1f, 0f, 0f], 5, null, MemorySearchFilter.None)).MemoryId);
            Assert.Equal("m1", Assert.Single(await store.KeywordSearchAsync(KeywordQuery.Build("zqtitlemarker", stemmed: false)!, 5, MemorySearchFilter.None)).MemoryId);
            Assert.Equal(StorageState.Open, store.State);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(StoredFileState.Encrypted, SqliteFileHeader.Classify(this.settings.DatabasePath));
        Assert.False(this.AnyFileContains("zqkeptmarker"));
        Assert.False(this.AnyFileContains("zqtitlemarker"));
        Assert.False(this.AnyFileContains("zqtagmarker"));
        Assert.False(this.AnyFileContains("zqdeletedmarker"));
    }

    [Fact]
    public async Task NewStore_WritesOnlyCiphertext()
    {
        using (var store = this.Encrypted(Ring()))
        {
            await store.StoreMemoryAsync("m1", "zqnewmarker", Chunk("m1", "zqnewtitle"), Vector(1));
            Assert.Equal("zqnewmarker", await store.ReadContentAsync(Path.Combine(this.settings.MemoriesDirectory, "m1.memory.data"), CancellationToken.None));
        }

        SqliteConnection.ClearAllPools();
        Assert.False(this.AnyFileContains("zqnewmarker"));
        Assert.False(this.AnyFileContains("zqnewtitle"));
        using var plain = new SqliteConnection($"Data Source={this.settings.DatabasePath};Pooling=False");
        plain.Open();
        using var cmd = plain.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks;";
        Assert.Equal(26, Assert.Throws<SqliteException>(() => cmd.ExecuteScalar()).SqliteErrorCode);
    }

    [Fact]
    public async Task WrongKey_FailsClosedAndNeverTouchesAFile()
    {
        using (var store = this.Encrypted(Ring(seed: 1)))
        {
            await store.StoreMemoryAsync("m1", "x", Chunk("m1", null), Vector(1));
        }

        SqliteConnection.ClearAllPools();
        var before = this.HashAllFiles();

        using var wrong = this.Encrypted(Ring(seed: 2));
        await Assert.ThrowsAsync<StorageKeyMismatchException>(() => wrong.GetMemoryAsync("m1"));
        await Assert.ThrowsAsync<StorageLockedException>(() => wrong.GetMemoryAsync("m1"));

        Assert.Equal(StorageState.Failed, wrong.State);
        Assert.Equal(before, this.HashAllFiles());
    }

    [Fact]
    public async Task NoKey_LockedAndNothingCreated()
    {
        using var store = this.Encrypted(new StorageKeyRing(), TimeSpan.FromMilliseconds(50));

        await store.InitializeAsync();
        await Assert.ThrowsAsync<StorageLockedException>(() => store.GetMemoryAsync("m1"));

        Assert.Equal(StorageState.AwaitingKey, store.State);
        Assert.False(File.Exists(this.settings.DatabasePath));
    }

    [Theory]
    [InlineData("before-rekey")]
    [InlineData("after-encrypted-copy")]
    [InlineData("after-plaintext-overwrite")]
    public async Task CrashMidMigration_CompletedOnTheNextOpen(string stage)
    {
        await this.SeedPlaintextAsync();

        using (var crashing = this.Encrypted(Ring()))
        {
            crashing.FaultInjector = s => { if (s == stage) { throw new IOException("power loss"); } };
            await Assert.ThrowsAsync<IOException>(() => crashing.GetMemoryAsync("m1"));
        }

        SqliteConnection.ClearAllPools();
        using (var store = this.Encrypted(Ring()))
        {
            Assert.Equal("zqkeptmarker alpha", (await store.GetMemoryAsync("m1"))!.Content);
        }

        SqliteConnection.ClearAllPools();
        Assert.False(this.AnyFileContains("zqkeptmarker"));
        Assert.False(this.AnyFileContains("zqtitlemarker"));
    }
}
