using System.Text;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.Integrity;

/// <summary>
/// Increment 3 integrity gate: chunks project incrementally exactly as a rebuild, no vector ever
/// describes text that is not in the source, and vector bytes are erased and encrypted like the index.
/// </summary>
[Trait("Category", "Integrity")]
public sealed class DenseIntegrityTests : IAsyncDisposable
{
    private readonly FakeHistoryEmbedder embedder = new();
    private readonly HistoryHarness h;

    public DenseIntegrityTests()
    {
        this.h = new HistoryHarness(DenseOptions(), this.embedder);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private static HistoryOptions DenseOptions()
    {
        var options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        options.Dense.Enabled = true;
        options.Dense.ChunkWords = 32;
        options.Dense.ChunkOverlapWords = 4;
        return options;
    }

    private static byte[] Needle(float[] vector) => VectorMath.ToBlob(vector)[..32];

    private static byte[] QuantizedNeedle(float[] vector) => VectorMath.QuantizeInt8(vector)[..36];

    private static bool AnyFileContains(string directory, byte[] needle) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray().AsSpan().IndexOf(needle) >= 0;
        });

    private static async Task EmbedAllAsync(HistoryProjector projector, FakeTimeProvider time)
    {
        time.Advance(TimeSpan.FromMinutes(3));
        while ((await projector.EmbedPendingAsync(CancellationToken.None)).Published > 0)
        {
        }
    }

    private float[] EmbeddedVectorOf(string channel)
    {
        using var reader = this.h.Projector.Current!.OpenReader();
        var chunk = reader.ReadChunks(channel).Single();
        Assert.True(chunk.HasVector);
        return reader.ReadVector(chunk.TextHash)!;
    }

    [Fact]
    public async Task IncrementalWithEmbedding_RandomOperations_ChunksEqualRebuildAndNoVectorIsStale()
    {
        var random = new Random(20261121);
        string[] vocabulary = ["oslo", "tog", "søndag", "поезд", "meeting", "budget", "kaffe", "plan", "railway", "invoice"];
        string[] channels = ["a", "b"];
        string Text() => string.Join(' ', Enumerable.Range(0, random.Next(1, 30)).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
        var ids = new List<long>();
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();

        for (var op = 0; op < 120; op++)
        {
            var channel = channels[random.Next(channels.Length)];
            switch (random.Next(6))
            {
                case 0 or 1 or 2:
                    ids.Add(await this.h.SayAsync(Text(), random.Next(0, 3000), channel, random.Next(2) == 0 ? "user" : "assistant", (MessageCategory)random.Next(0, 4)));
                    break;
                case 3 when ids.Count > 0:
                    await this.h.Store.UpdateContentAsync(ids[random.Next(ids.Count)], Text());
                    break;
                case 4:
                    await this.h.Store.DeleteChannelMessagesOlderThanAsync(channel, HistoryHarness.T0.AddMinutes(random.Next(0, 400)));
                    break;
                default:
                    await EmbedAllAsync(this.h.Projector, this.h.Time);
                    break;
            }

            if (op % 7 == 0)
            {
                await this.h.Projector.ApplyPendingAsync(CancellationToken.None);
            }
        }

        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);
        await EmbedAllAsync(this.h.Projector, this.h.Time);

        using var reference = new HistoryProjector(
            this.h.Store, this.h.Store, new HistoryIndexGenerations(Path.Combine(this.h.Root, "reference")), new HistoryAccessGate(),
            this.h.Monitor, new HistoryStatus(), NullLogger<HistoryProjector>.Instance, this.embedder, this.h.Time);
        await reference.InitializeAsync(CancellationToken.None);
        await reference.RebuildAsync(CancellationToken.None);

        using var incremental = this.h.Projector.Current!.OpenReader();
        using var rebuilt = reference.Current!.OpenReader();
        static IEnumerable<string> Comparable(IEnumerable<StoredChunk> chunks) =>
            chunks.Select(c => $"{c.ChannelId}|{c.SegFirstMsgId}|{c.Ordinal}|{string.Join(',', c.MsgIds)}|{c.AllInternal}|{c.TextHash}|{c.IsOpen}");
        Assert.NotEmpty(rebuilt.ReadChunks());
        Assert.Equal(Comparable(rebuilt.ReadChunks()), Comparable(incremental.ReadChunks()));

        // No stale vector: every stored vector is the embedding of the chunk text recomputed from the source now.
        await using var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None);
        var settings = HistoryChunkSettings.From(this.h.Options.Dense);
        foreach (var segment in incremental.ReadSegments())
        {
            var rows = (await snapshot.ReadByIdsAsync(segment.MsgIds, CancellationToken.None)).OrderBy(r => r.Timestamp.UtcTicks).ThenBy(r => r.Id).ToList();
            foreach (var (chunk, text) in HistoryChunker.Chunk(rows, settings))
            {
                if (incremental.ReadVector(chunk.TextHash) is { } stored)
                {
                    Assert.Equal(VectorMath.Normalize(this.embedder.Vector(text)), stored);
                }
            }
        }
    }

    [Fact]
    public async Task ChannelPurge_VectorBytesGoneFromEveryIndexFile()
    {
        await this.h.SayAsync("zqvectormarker alpha beta gamma", 0, channel: "secret");
        await this.h.SayAsync("unrelated words", 0, channel: "kept");
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        await EmbedAllAsync(this.h.Projector, this.h.Time);
        var vector = this.EmbeddedVectorOf("secret");
        var directory = Path.Combine(this.h.Root, "history");
        Assert.True(AnyFileContains(directory, Needle(vector)));
        Assert.True(AnyFileContains(directory, QuantizedNeedle(vector)));

        await this.h.Store.DeleteChannelMessagesAsync("secret");
        await using (await this.h.Gate.AcquireWriteAsync(CancellationToken.None))
        {
            Assert.True(await this.h.Projector.EraseAsync(CancellationToken.None));
        }

        Assert.False(AnyFileContains(directory, Needle(vector)));
        Assert.False(AnyFileContains(directory, QuantizedNeedle(vector)));
    }

    [Fact]
    public async Task SameLengthEdit_OldVectorBytesGoneAfterReprojectionAndCleanup()
    {
        var id = await this.h.SayAsync("zqedit alpha beta", 0, channel: "edited");
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        await EmbedAllAsync(this.h.Projector, this.h.Time);
        var vector = this.EmbeddedVectorOf("edited");
        Assert.True(AnyFileContains(Path.Combine(this.h.Root, "history"), Needle(vector)));

        await this.h.Store.UpdateContentAsync(id, "zqedit gamma delt");
        await using (await this.h.Gate.AcquireWriteAsync(CancellationToken.None))
        {
            Assert.True(await this.h.Projector.EraseAsync(CancellationToken.None));
        }

        Assert.False(AnyFileContains(Path.Combine(this.h.Root, "history"), Needle(vector)));
    }

    [Fact]
    public async Task EncryptedGeneration_VectorsHaveNoPlaintextBytesOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dense-enc-{Guid.NewGuid():N}");
        var ring = new StorageKeyRing();
        ring.Provide(Convert.ToBase64String(Enumerable.Range(9, 32).Select(i => (byte)i).ToArray()));
        var monitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
        monitor.CurrentValue.Returns(DenseOptions());
        var time = new FakeTimeProvider(HistoryHarness.T0);
        await using var store = new MessageStore(
            Path.Combine(root, "messages.db"), NullLogger<MessageStore>.Instance, new MessageStoreEncryption(ring, TimeSpan.FromSeconds(5)));
        try
        {
            await store.SaveMessageAsync("u", "ch", "user", "zqencrypted alpha beta", HistoryHarness.T0);
            using var projector = new HistoryProjector(
                store, store, new HistoryIndexGenerations(Path.Combine(root, "history"), ring), new HistoryAccessGate(),
                monitor, new HistoryStatus(), NullLogger<HistoryProjector>.Instance, this.embedder, time);
            await projector.InitializeAsync(CancellationToken.None);
            await projector.RebuildAsync(CancellationToken.None);
            await EmbedAllAsync(projector, time);

            float[] vector;
            using (var reader = projector.Current!.OpenReader())
            {
                vector = reader.ReadVector(reader.ReadChunks().Single().TextHash)!;
            }

            Assert.False(AnyFileContains(Path.Combine(root, "history"), Needle(vector)));
            Assert.False(AnyFileContains(Path.Combine(root, "history"), Encoding.UTF8.GetBytes("zqencrypted")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await store.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
