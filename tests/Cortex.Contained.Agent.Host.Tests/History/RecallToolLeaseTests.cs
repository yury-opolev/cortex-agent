using System.Text.Json;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>§10 read barrier: a recall tool keeps its read lease until its response is rendered; the legacy <c>history_read</c> is gated too.</summary>
public sealed class RecallToolLeaseTests : IAsyncDisposable
{
    private static readonly ToolExecutionContext Context = new() { ConversationId = "conv", ChannelId = "ch" };

    private readonly HistoryHarness h = new();
    private readonly HistoryToolsSwitch toolsSwitch;
    private readonly HistoryRecallService recall;
    private readonly ManualResetEventSlim renderEntered = new();
    private readonly ManualResetEventSlim renderRelease = new();

    public RecallToolLeaseTests()
    {
        this.toolsSwitch = new HistoryToolsSwitch(this.h.Monitor);
        this.recall = new HistoryRecallService(this.h.Store, this.h.Projector, this.h.Gate, this.h.Status, this.h.Monitor);
    }

    public ValueTask DisposeAsync()
    {
        this.renderRelease.Set();
        this.renderEntered.Dispose();
        this.renderRelease.Dispose();
        return this.h.DisposeAsync();
    }

    // The boundary factory runs while the result is rendered, so blocking in it simulates a slow render.
    private string BlockingBoundary()
    {
        this.renderEntered.Set();
        this.renderRelease.Wait(TimeSpan.FromSeconds(20));
        return "HBfixedboundary0003";
    }

    private async Task IndexAsync()
    {
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
    }

    private async Task AssertPurgeWaitsForRenderAsync(Func<Task<AgentToolResult>> run)
    {
        var rendering = Task.Run(run);
        Assert.True(this.renderEntered.Wait(TimeSpan.FromSeconds(10)));

        var writing = this.h.Gate.AcquireWriteAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.False(writing.IsCompleted);

        this.renderRelease.Set();
        Assert.True((await rendering).Success);
        await using (await writing)
        {
        }
    }

    [Fact]
    public async Task HistorySearch_Render_HoldsReadLeaseUntilRendered()
    {
        await this.h.SayAsync("the ferry to TownA", 0);
        await this.IndexAsync();
        var tool = new HistorySearchTool(this.recall, this.toolsSwitch, this.h.Monitor, this.BlockingBoundary);

        await this.AssertPurgeWaitsForRenderAsync(() => tool.ExecuteAsync("""{"query":"TownA"}""", Context, CancellationToken.None));
    }

    [Fact]
    public async Task HistoryRead_RecallRender_HoldsReadLeaseUntilRendered()
    {
        await this.h.SayAsync("the ferry to TownA", 0);
        await this.IndexAsync();
        var tool = new HistoryReadTool(this.h.Store, this.recall, this.toolsSwitch, this.h.Monitor, this.BlockingBoundary, this.h.Gate);

        await this.AssertPurgeWaitsForRenderAsync(() => tool.ExecuteAsync("""{"channelId":"ch"}""", Context, CancellationToken.None));
    }

    [Fact]
    public async Task EventGet_Render_HoldsReadLeaseUntilRendered()
    {
        var rowId = await this.h.SayAsync("the ferry to TownA", 0);
        await using var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None);
        var eventId = (await snapshot.ReadMessageEventsAsync(rowId, CancellationToken.None))[0].Id;
        var tool = new EventGetTool(new EventRecallService(this.h.Store, this.h.Gate), this.toolsSwitch, this.h.Monitor, this.BlockingBoundary);

        await this.AssertPurgeWaitsForRenderAsync(() => tool.ExecuteAsync(JsonSerializer.Serialize(new { id = eventId }), Context, CancellationToken.None));
    }

    [Fact]
    public async Task HistoryRead_LegacyMode_WaitsForAPurgeInProgress()
    {
        await this.h.SayAsync("the ferry to TownA", 0);
        var tool = new HistoryReadTool(this.h.Store, gate: this.h.Gate);

        var writeLease = await this.h.Gate.AcquireWriteAsync(CancellationToken.None);
        var reading = tool.ExecuteAsync("""{"channelId":"ch"}""", Context, CancellationToken.None);
        await Task.Delay(200);
        Assert.False(reading.IsCompleted);

        await writeLease.DisposeAsync();
        var result = await reading;

        Assert.True(result.Success);
        Assert.Contains("TownA", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryRead_LegacyMode_ReleasesTheLeaseAfterwards()
    {
        await this.h.SayAsync("the ferry to TownA", 0);
        var tool = new HistoryReadTool(this.h.Store, gate: this.h.Gate);

        await tool.ExecuteAsync("""{"channelId":"ch"}""", Context, CancellationToken.None);

        Assert.Equal(0, this.h.Gate.ActiveReaders);
    }
}
