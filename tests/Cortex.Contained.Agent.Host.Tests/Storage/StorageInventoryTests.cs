using System.Security.Cryptography;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

public sealed class StorageInventoryTests : IDisposable
{
    private static readonly byte[] SqliteHeader = [.. "SQLite format 3\0"u8.ToArray(), .. new byte[4080]];

    private readonly string root = Path.Combine(Path.GetTempPath(), $"inventory-{Guid.NewGuid():N}");
    private readonly StorageLayout layout;

    public StorageInventoryTests()
    {
        this.layout = new StorageLayout(
            Path.Combine(this.root, "state"),
            Path.Combine(this.root, "data"),
            Path.Combine(this.root, "state", "memory", "memory.db"),
            Path.Combine(this.root, "state", "memory", "memories"));
        Directory.CreateDirectory(this.layout.MemoriesDirectory);
        Directory.CreateDirectory(this.layout.HistoryDirectory);
        Directory.CreateDirectory(Path.Combine(this.layout.DataRoot, "tool-output"));
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void Covered_ClassifiesEachStoreByItsBytes()
    {
        var cipher = new StorageFileCipher(RandomNumberGenerator.GetBytes(32), "memory-content");
        Write(this.layout.MessagesDatabase, SqliteHeader);
        Write(this.layout.MessagesDatabase + "-wal", new byte[100]);
        Write(this.layout.MemoryDatabasePath, RandomNumberGenerator.GetBytes(4096));
        Write(Path.Combine(this.layout.HistoryDirectory, "history-index.1.db"), SqliteHeader);
        Write(Path.Combine(this.layout.HistoryDirectory, "history-index.2.db"), RandomNumberGenerator.GetBytes(4096));
        Write(Path.Combine(this.layout.MemoriesDirectory, "a.memory.data"), "plain"u8.ToArray());
        Write(Path.Combine(this.layout.MemoriesDirectory, "b.memory.data"), cipher.Encrypt("x"u8.ToArray()));

        var items = new StorageInventory(this.layout).Covered().ToDictionary(i => i.Id);

        Assert.Equal(StorageItemStates.Plaintext, items["messages"].State);
        Assert.Equal(SqliteHeader.Length + 100, items["messages"].Bytes);
        Assert.Equal(StorageItemStates.Encrypted, items["memory-db"].State);
        Assert.Equal("memory/memory.db", items["memory-db"].Path);
        Assert.Equal(StorageItemStates.Mixed, items["history-index"].State);
        Assert.Equal(StorageItemStates.Mixed, items["memory-content"].State);
        Assert.Equal(2, items["memory-content"].Files);
        Assert.Equal(StorageItemStates.Absent, items["sessions-snapshot"].State);
        Assert.True(new StorageInventory(this.layout).HasEncryptedData());
    }

    [Fact]
    public void NotCoveredAndEvaluation_ListKnownPlaintextStoresAndUnrecognizedEntries()
    {
        Write(Path.Combine(this.layout.StateRoot, "subagents", "subagents.db"), SqliteHeader);
        Write(Path.Combine(this.layout.StateRoot, "backups-old", "copy.memory.data"), "x"u8.ToArray());
        Write(Path.Combine(this.layout.StateRoot, "recall-settings.json"), "{}"u8.ToArray());
        Write(Path.Combine(this.layout.EvaluationDirectory, "hybrid", "runs", "r1", "messages.db"), SqliteHeader);
        Write(Path.Combine(this.layout.DataRoot, "tool-output", "big.txt"), "x"u8.ToArray());
        var inventory = new StorageInventory(this.layout);

        var notCovered = inventory.NotCovered().ToDictionary(i => i.Id);
        var evaluation = inventory.Evaluation();

        Assert.Equal(StorageItemStates.NotCovered, notCovered["subagents"].State);
        Assert.Contains("other:backups-old", notCovered.Keys);
        Assert.Contains("tool-output", notCovered.Keys);
        Assert.DoesNotContain("other:recall-settings.json", notCovered.Keys);
        Assert.DoesNotContain("other:memory", notCovered.Keys);
        Assert.Equal(1, evaluation!.Files);
        Assert.Equal(StorageItemStates.Plaintext, evaluation.State);
    }

    [Fact]
    public void EmptyState_NothingEncryptedNoEvaluation()
    {
        var inventory = new StorageInventory(this.layout);

        Assert.False(inventory.HasEncryptedData());
        Assert.Null(inventory.Evaluation());
        Assert.All(inventory.Covered(), i => Assert.Equal(StorageItemStates.Absent, i.State));
    }

    public static TheoryData<Exception> ReadFailures => new()
    {
        new FileNotFoundException("gone"),
        new DirectoryNotFoundException("gone"),
        new UnauthorizedAccessException("denied"),
    };

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void Covered_AFileCannotBeRead_ThatItemIsUnknownAndTheScanContinues(Exception failure)
    {
        Write(this.layout.MessagesDatabase, SqliteHeader);
        Write(Path.Combine(this.layout.MemoriesDirectory, "a.memory.data"), "plain"u8.ToArray());
        var inventory = new StorageInventory(this.layout)
        {
            BeforeRead = path => { if (path.EndsWith("messages.db", StringComparison.Ordinal)) { throw failure; } },
        };

        var items = inventory.Covered().ToDictionary(i => i.Id);

        Assert.Equal(StorageItemStates.Unknown, items["messages"].State);
        Assert.Equal(StorageItemStates.Plaintext, items["memory-content"].State);
    }

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void Covered_AContentFileVanishes_TheGroupIsUnknown(Exception failure)
    {
        Write(Path.Combine(this.layout.MemoriesDirectory, "a.memory.data"), "plain"u8.ToArray());
        Write(Path.Combine(this.layout.MemoriesDirectory, "b.memory.data"), "plain"u8.ToArray());
        var inventory = new StorageInventory(this.layout)
        {
            BeforeRead = path => { if (path.EndsWith("b.memory.data", StringComparison.Ordinal)) { throw failure; } },
        };

        Assert.Equal(StorageItemStates.Unknown, inventory.Covered().Single(i => i.Id == "memory-content").State);
    }

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void NotCoveredAndEvaluation_AFolderCannotBeRead_AreSkippedWithoutThrowing(Exception failure)
    {
        Write(Path.Combine(this.layout.StateRoot, "subagents", "subagents.db"), SqliteHeader);
        Write(Path.Combine(this.layout.EvaluationDirectory, "hybrid", "messages.db"), SqliteHeader);
        var inventory = new StorageInventory(this.layout) { BeforeRead = _ => throw failure };

        Assert.DoesNotContain(inventory.NotCovered(), i => i.Id == "subagents");
        Assert.Null(inventory.Evaluation());
    }
}
