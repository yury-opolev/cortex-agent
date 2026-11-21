using System.Text;
using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Tests.History;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Integrity;

/// <summary>Byte-level erasure including the event log and the spill files its tool results reference.</summary>
[Trait("Category", "Integrity")]
public sealed class EventErasureTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();
    private readonly string toolOutput;
    private readonly HistoryPurgeService purge;

    public EventErasureTests()
    {
        this.toolOutput = Path.Combine(this.h.Root, "tool-output");
        Directory.CreateDirectory(this.toolOutput);
        this.purge = new HistoryPurgeService(
            this.h.Store,
            this.h.Gate,
            this.h.Projector,
            [
                new IndexErasureStep(this.h.Projector),
                new MessagesWalErasureStep(this.h.Store),
                new ToolOutputErasureStep(this.toolOutput),
            ],
            this.h.Monitor,
            TimeProvider.System,
            NullLogger<HistoryPurgeService>.Instance);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private bool AnyFileContains(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        return Directory.EnumerateFiles(this.h.Root, "*", SearchOption.AllDirectories).Any(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray().AsSpan().IndexOf(needle) >= 0;
        });
    }

    [Fact]
    public async Task ChannelPurge_ErasesMessagesEventDigestsAndReferencedSpillFiles()
    {
        await this.h.SayAsync("zqmsgmarker", 0, channel: "secret");
        var run = new RunProvenance("run_e", AuthorKinds.Main, null, "secret", null);
        await this.h.Store.AppendAsync(
            EventDraft.ForRun(EventTypes.ToolResult, run) with
            {
                Payload = new JsonObject { ["tool"] = "date_time", ["digest"] = "zqdigestmarker", ["spill"] = "20261121-secret.txt" },
            },
            CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(this.toolOutput, "20261121-secret.txt"), "zqspillmarker");
        await File.WriteAllTextAsync(Path.Combine(this.toolOutput, "20261121-other.txt"), "kept");
        await this.h.SayAsync("keep words", 0, channel: "other");
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        Assert.True(this.AnyFileContains("zqdigestmarker"));

        var result = await this.purge.PurgeAsync(new HistoryPurgeScope("secret", null), null, CancellationToken.None);

        Assert.True(result.ErasureComplete);
        Assert.False(this.AnyFileContains("zqmsgmarker"));
        Assert.False(this.AnyFileContains("zqdigestmarker"));
        Assert.False(this.AnyFileContains("zqspillmarker"));
        Assert.True(File.Exists(Path.Combine(this.toolOutput, "20261121-other.txt")));
        await IntegrityAssertions.AssertEventLogConsistentAsync(this.h.Store);
    }

    [Fact]
    public async Task CorrectedMessage_PurgeErasesBothVersions()
    {
        var id = await this.h.SayAsync("spoken zqtailmarker", 0, channel: "voice", role: "assistant");
        await this.h.Store.UpdateContentAsync(id, "spoken…");
        await this.h.SayAsync("other", 0, channel: "other");
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();

        await this.purge.PurgeAsync(new HistoryPurgeScope("voice", null), null, CancellationToken.None);

        Assert.False(this.AnyFileContains("zqtailmarker"));
        Assert.False(this.AnyFileContains("spoken"));
    }
}
