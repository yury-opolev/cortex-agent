using System.Text.Json;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary><c>history_search</c> mode parameter and hybrid rendering.</summary>
public sealed class HistorySearchToolHybridTests : IAsyncDisposable
{
    private static readonly ToolExecutionContext Context = new() { ConversationId = "conv", ChannelId = "ch" };

    private readonly FakeHistoryEmbedder embedder = new(new Dictionary<string, string> { ["train"] = "railway", ["trip"] = "journey" });
    private readonly HistoryHarness h;
    private readonly HistorySearchTool tool;

    public HistorySearchToolHybridTests()
    {
        var options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        options.Dense.Enabled = true;
        this.h = new HistoryHarness(options, this.embedder);
        var recall = new HistoryRecallService(this.h.Store, this.h.Projector, this.h.Gate, this.h.Status, this.h.Monitor, this.embedder, this.h.Time);
        var toolsSwitch = new HistoryToolsSwitch(this.h.Monitor);
        this.tool = new HistorySearchTool(recall, toolsSwitch, this.h.Monitor, () => "HBfixedboundary0002");
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task SeedAndEmbedAsync()
    {
        await this.h.SayAsync("we booked the railway journey to TownB", 0);
        await this.h.SayAsync("invoice INV2041 is paid", 200);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        this.h.Time.Advance(TimeSpan.FromMinutes(3));
        while ((await this.h.Projector.EmbedPendingAsync(CancellationToken.None)).Published > 0)
        {
        }
    }

    private Task<AgentToolResult> RunAsync(object args) => this.tool.ExecuteAsync(JsonSerializer.Serialize(args), Context, CancellationToken.None);

    [Fact]
    public async Task ExecuteAsync_InvalidMode_Fails()
    {
        var result = await this.RunAsync(new { query = "x", mode = "fuzzy" });

        Assert.False(result.Success);
        Assert.Contains("mode", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_Hybrid_RendersMatchKindModeAndDenseCoverage()
    {
        await this.SeedAndEmbedAsync();

        var result = await this.RunAsync(new { query = "train trip" });

        Assert.True(result.Success);
        Assert.Contains("| match semantic", result.Content, StringComparison.Ordinal);
        Assert.Contains("mode: hybrid (keyword + semantic)", result.Content, StringComparison.Ordinal);
        Assert.Contains("dense: on embedded=2/2 pending=0 failed=0", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_KeywordMode_NoMatchKindAndSaysWhyDenseUnused()
    {
        await this.SeedAndEmbedAsync();

        var result = await this.RunAsync(new { query = "invoice", mode = "keyword" });

        Assert.DoesNotContain("| match ", result.Content, StringComparison.Ordinal);
        Assert.Contains("mode: keyword", result.Content, StringComparison.Ordinal);
        Assert.Contains("dense: not used: mode=keyword", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_ExplainsModesAndKeepsRequiredWording()
    {
        Assert.Contains("mode=keyword", HistorySearchTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("paraphrases", HistorySearchTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("Do not infer invention from a missing source.", HistorySearchTool.ToolDescription, StringComparison.Ordinal);
        Assert.Contains("\"mode\"", HistorySearchTool.ToolSchema, StringComparison.Ordinal);
    }
}
