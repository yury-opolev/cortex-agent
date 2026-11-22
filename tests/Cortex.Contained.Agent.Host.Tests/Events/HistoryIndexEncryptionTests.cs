using System.Text;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>Index generations are encrypted too; plaintext generations are erased and rebuilt (D4).</summary>
[Trait("Category", "Integrity")]
public sealed class HistoryIndexEncryptionTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"index-enc-{Guid.NewGuid():N}");
    private readonly IOptionsMonitor<HistoryOptions> monitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
    private readonly StorageKeyRing ring = new();
    private readonly MessageStore store;

    public HistoryIndexEncryptionTests()
    {
        this.monitor.CurrentValue.Returns(new HistoryOptions { Enabled = true, TimeZone = "UTC" });
        this.ring.Provide(Convert.ToBase64String(Enumerable.Range(7, 32).Select(i => (byte)i).ToArray()));
        this.store = new MessageStore(
            Path.Combine(this.root, "messages.db"),
            NullLogger<MessageStore>.Instance,
            new MessageStoreEncryption(this.ring, TimeSpan.FromSeconds(5)));
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
    }

    private HistoryProjector Projector(HistoryIndexGenerations generations) =>
        new(this.store, this.store, generations, new HistoryAccessGate(), this.monitor, new HistoryStatus(), NullLogger<HistoryProjector>.Instance);

    private static async Task BuildAsync(HistoryProjector projector)
    {
        await projector.InitializeAsync(CancellationToken.None);
        await projector.RebuildAsync(CancellationToken.None);
        await projector.ApplyPendingAsync(CancellationToken.None);
    }

    private bool AnyFileContains(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        return Directory.EnumerateFiles(this.root, "*", SearchOption.AllDirectories).Any(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray().AsSpan().IndexOf(needle) >= 0;
        });
    }

    [Fact]
    public async Task EncryptedGeneration_IsSearchableAndHoldsNoPlaintextTerms()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "zqindexmarker words", T0);
        using var projector = this.Projector(new HistoryIndexGenerations(Path.Combine(this.root, "history"), this.ring));

        await BuildAsync(projector);

        using (var reader = projector.Current!.OpenReader())
        {
            Assert.Contains("zqindexmarker", reader.ReadTerms());
        }

        Assert.False(this.AnyFileContains("zqindexmarker"));
    }

    [Fact]
    public async Task PlaintextGeneration_IsErasedAndRebuiltEncrypted()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "zqplainindex words", T0);
        var directory = Path.Combine(this.root, "history");
        using (var plaintext = this.Projector(new HistoryIndexGenerations(directory)))
        {
            await BuildAsync(plaintext);
        }

        Assert.True(this.AnyFileContains("zqplainindex"));

        using var projector = this.Projector(new HistoryIndexGenerations(directory, this.ring));
        await projector.InitializeAsync(CancellationToken.None);
        Assert.True(projector.NeedsRebuild);
        await projector.RebuildAsync(CancellationToken.None);

        Assert.False(this.AnyFileContains("zqplainindex"));
        Assert.Empty(await this.store.ReadPendingErasureAsync(CancellationToken.None));
    }
}
