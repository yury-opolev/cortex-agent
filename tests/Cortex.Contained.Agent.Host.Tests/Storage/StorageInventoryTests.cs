using System.Security.Cryptography;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
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
        Assert.Equal(StorageItemStates.Plaintext, inventory.Evaluation(encryptedCopies: true)!.State);
    }

    [Fact]
    public void Evaluation_EncryptedInstallWithOnlySealedFilesAndReports_IsEncrypted()
    {
        var cipher = new StorageFileCipher(RandomNumberGenerator.GetBytes(32), "eval-files/dataset.jsonl");
        var eval = this.layout.EvaluationDirectory;
        Write(Path.Combine(eval, "frozen", "v1", "messages.db"), RandomNumberGenerator.GetBytes(4096));
        Write(Path.Combine(eval, "frozen", "v1", "history", "dataset.jsonl"), cipher.Encrypt("{}"u8.ToArray()));
        Write(Path.Combine(eval, "frozen", "v1", "history", "dataset.example.jsonl"), "{}"u8.ToArray());
        Write(Path.Combine(eval, "history", "runs", "r1", "report.json"), "{}"u8.ToArray());
        new FrozenCorpusManifest { Version = "v1", Encrypted = true }.Write(Path.Combine(eval, "frozen", "v1", FrozenCorpusManifest.FileName));

        var evaluation = new StorageInventory(this.layout).Evaluation(encryptedCopies: true)!;

        Assert.Equal(StorageItemStates.Encrypted, evaluation.State);
        Assert.DoesNotContain("erase", evaluation.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluation_EncryptedInstallWithOnlyReports_IsEncrypted()
    {
        Write(Path.Combine(this.layout.EvaluationDirectory, "history", "runs", "r1", "report.json"), "{}"u8.ToArray());

        Assert.Equal(StorageItemStates.Encrypted, new StorageInventory(this.layout).Evaluation(encryptedCopies: true)!.State);
    }

    [Fact]
    public void Evaluation_EncryptedInstallWithLeftoverPlaintextCopies_IsPlaintextOrMixedWithAnEraseHint()
    {
        var eval = this.layout.EvaluationDirectory;
        Write(Path.Combine(eval, "hybrid", "runs", "r1", "messages.db"), SqliteHeader);
        var inventory = new StorageInventory(this.layout);

        var plaintext = inventory.Evaluation(encryptedCopies: true)!;
        Write(Path.Combine(eval, "frozen", "v1", "messages.db"), RandomNumberGenerator.GetBytes(4096));
        var mixed = inventory.Evaluation(encryptedCopies: true)!;

        Assert.Equal(StorageItemStates.Plaintext, plaintext.State);
        Assert.Equal(StorageItemStates.Mixed, mixed.State);
        Assert.Contains("erase", mixed.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("erase", plaintext.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("history/dataset.jsonl")]
    [InlineData("memories.jsonl")]
    [InlineData("secret.key")]
    [InlineData("memory/abc.memory.data")]
    public void Evaluation_EncryptedInstallWithAPlaintextTextFile_IsNotEncrypted(string name)
    {
        var eval = this.layout.EvaluationDirectory;
        Write(Path.Combine(eval, "frozen", "v1", "messages.db"), RandomNumberGenerator.GetBytes(4096));
        Write(Path.Combine(eval, "frozen", "v1", name), "plain text"u8.ToArray());

        Assert.Equal(StorageItemStates.Mixed, new StorageInventory(this.layout).Evaluation(encryptedCopies: true)!.State);
    }

    [Fact]
    public void Evaluation_FrozenManifestOfAPlaintextVersion_IsNotEncrypted()
    {
        var eval = this.layout.EvaluationDirectory;
        Write(Path.Combine(eval, "frozen", "v1", "messages.db"), RandomNumberGenerator.GetBytes(4096));
        new FrozenCorpusManifest { Version = "v1", Encrypted = false }.Write(Path.Combine(eval, "frozen", "v1", FrozenCorpusManifest.FileName));

        Assert.Equal(StorageItemStates.Mixed, new StorageInventory(this.layout).Evaluation(encryptedCopies: true)!.State);
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

    [Fact]
    public void SnapshotSetAsideByAShutdown_IsPartOfTheSnapshotItemNotAnUnrecognizedEntry()
    {
        var cipher = new StorageFileCipher(RandomNumberGenerator.GetBytes(32), "sessions-snapshot");
        Write(Path.Combine(this.layout.StateRoot, "sessions-snapshot.pending-20261122100000000.enc"), cipher.Encrypt("{}"u8.ToArray()));
        var inventory = new StorageInventory(this.layout);

        var snapshot = inventory.Covered().Single(i => i.Id == "sessions-snapshot");

        Assert.Equal(StorageItemStates.Encrypted, snapshot.State);
        Assert.True(snapshot.Bytes > 0);
        Assert.DoesNotContain(inventory.NotCovered(), i => i.Id.StartsWith("other:", StringComparison.Ordinal));
    }
}
