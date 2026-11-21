using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

/// <summary>Characterises the memory_search output, which the default (semantic) mode must keep byte for byte.</summary>
public class MemorySearchToolTests
{
    private static readonly ToolExecutionContext Context = new() { ConversationId = "conv", ChannelId = "ch" };
    private static readonly DateTimeOffset Created = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly IMemoryService memoryService = Substitute.For<IMemoryService>();
    private readonly MemoryMcpOptions options = new() { SearchMaxContentLength = 20 };

    private static SearchResult Result(string id, float score, string content, string? title = null, List<string>? tags = null) => new()
    {
        MemoryId = id,
        Title = title,
        Content = content,
        Tags = tags ?? [],
        CreatedAt = Created,
        UpdatedAt = Created.AddDays(1),
        Score = score,
    };

    private MemorySearchTool Tool() => new(this.memoryService, Substitute.For<IEmbeddingService>(), Options.Create(this.options));

    private async Task<AgentToolResult> RunAsync(string json) =>
        await this.Tool().ExecuteAsync(json, Context, CancellationToken.None);

    [Fact]
    public async Task ExecuteAsync_Results_KeepTheSemanticOutputFormat()
    {
        this.memoryService.SearchAsync("trip", 5, null, null, Arg.Any<CancellationToken>()).Returns(
        [
            Result("m1", 0.91234f, "Night train on Friday", "Trip", ["travel", "plans"]),
            Result("m2", 0.5f, "short"),
        ]);

        var result = await this.RunAsync("""{"query":"trip"}""");

        Assert.True(result.Success);
        Assert.Equal(
            """
            Found 2 matching memories:

            --- Memory: m1 (Score: 0.9123) ---
            Title: Trip
            Tags: travel, plans
            Created: 2026-05-04 08:00:00Z | Updated: 2026-05-05 08:00:00Z
            Night train on Frida
            [truncated] Use memory_get with id "m1" to read full content.

            --- Memory: m2 (Score: 0.5000) ---
            Created: 2026-05-04 08:00:00Z | Updated: 2026-05-05 08:00:00Z
            short
            """.ReplaceLineEndings("\n"),
            result.Content.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task ExecuteAsync_NoResults_SaysSo()
    {
        this.memoryService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await this.RunAsync("""{"query":"nothing"}""");

        Assert.Equal("No matching memories found.", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_PassesLimitMinScoreAndTags()
    {
        this.memoryService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>()).Returns([]);

        await this.RunAsync("""{"query":"q","limit":3,"min_score":0.4,"tags":"[\"project\"]"}""");

        await this.memoryService.Received(1).SearchAsync("q", 3, 0.4f, Arg.Is<List<string>?>(t => t!.Single() == "project"), Arg.Any<CancellationToken>());
    }
}
