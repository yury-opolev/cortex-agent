using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>Readers that bypass IMemoryStore (memory list, quarantine) and the opener that migrates after the key push.</summary>
[Trait("Category", "Integrity")]
public sealed class EncryptedMemoryAccessTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly string root = Path.Combine(Path.GetTempPath(), $"memaccess-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions settings;

    public EncryptedMemoryAccessTests()
    {
        this.settings = new MemoryMcpOptions
        {
            DataDirectory = Path.Combine(this.root, "state", "memory"),
            Ollama = new OllamaOptions { Dimensions = 4 },
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static List<ChunkRecord> Chunk(string memoryId) =>
    [
        new ChunkRecord { MemoryId = memoryId, ChunkIndex = 0, StartOffset = 0, Length = 5, Title = "t", Tags = [], CreatedAt = T0, UpdatedAt = T0 },
    ];

    [Fact]
    public async Task MemoryManagementService_ListsAndTimesThroughTheEncryptedStore()
    {
        var ring = new StorageKeyRing();
        ring.Provide(Key);
        using var store = new EncryptedMemoryStore(Options.Create(this.settings), ring, TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);
        await store.StoreMemoryAsync("m1", "zqlistmarker", Chunk("m1"), [[1f, 0f, 0f, 0f]]);
        using var management = new MemoryManagementService(
            Substitute.For<IMemoryService>(), Options.Create(this.settings), NullLogger<MemoryManagementService>.Instance, store);

        var list = await management.ListAsync();
        var times = await management.ListCreationTimesAsync(CancellationToken.None);

        Assert.Equal("zqlistmarker", Assert.Single(list.Items).Content);
        Assert.Equal(T0, Assert.Single(times).CreatedAt);
    }

    [Fact]
    public async Task Opener_KeyArrives_OpensBothStoresAndRecordsTheFingerprint()
    {
        var layout = StorageLayout.From(Path.Combine(this.root, "state"), Path.Combine(this.root, "data"), this.settings);
        Directory.CreateDirectory(layout.StateRoot);
        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = true }, layout);
        var ring = new StorageKeyRing();
        await using var messages = new MessageStore(layout.MessagesDatabase, NullLogger<MessageStore>.Instance, new MessageStoreEncryption(ring, TimeSpan.FromSeconds(5)));
        using var memory = new EncryptedMemoryStore(Options.Create(this.settings), ring, TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);
        using var opener = new EncryptedStorageOpener(context, ring, messages, memory, TimeProvider.System, NullLogger<EncryptedStorageOpener>.Instance);

        await opener.StartAsync(CancellationToken.None);
        ring.Provide(Key);
        await opener.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StorageState.Open, messages.State);
        Assert.Equal(StorageState.Open, memory.State);
        Assert.Equal(StorageKeyFingerprint.Compute(Key), StorageEncryptionMarker.Read(layout.Marker)!.KeyFingerprint);
    }
}
