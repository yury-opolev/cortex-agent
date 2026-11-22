using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>memory_search</c> with the rerank stage after hybrid fusion (§4.3).</summary>
public sealed class MemoryRerankTests
{
    private static readonly ToolExecutionContext Context = new() { ConversationId = "conv", ChannelId = "ch" };
    private static readonly DateTimeOffset Created = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly IHybridMemorySearch search = Substitute.For<IHybridMemorySearch>();
    private readonly FakeReranker reranker = new();
    private readonly RerankerOptions rerankerOptions = new();
    private readonly RerankerAccess access = new();
    private readonly RerankStage stage;

    public MemoryRerankTests()
    {
        var monitor = Substitute.For<IOptionsMonitor<RerankerOptions>>();
        monitor.CurrentValue.Returns(this.rerankerOptions);
        this.access.Apply(new RerankerGrant { Enabled = true, ApiKey = "k" });
        this.rerankerOptions.Memory.TopN = 3;
        this.stage = new RerankStage(this.reranker, monitor, this.access, NullLogger<RerankStage>.Instance);
    }

    private static HybridSearchHit Hit(string id, string content, string? title = null) => new(
        new SearchResult { MemoryId = id, Title = title, Content = content, Tags = [], CreatedAt = Created, UpdatedAt = Created, Score = 0 },
        MemoryMatchKind.Keyword, 0.03, 1, null, null);

    private void Returns(MemorySearchMode mode, params HybridSearchHit[] hits) =>
        this.search.SearchAsync(Arg.Any<HybridSearchRequest>(), Arg.Any<CancellationToken>()).Returns(new HybridSearchResult(hits, mode, null, null));

    private Task<AgentToolResult> RunAsync(string json, IRerankStage? reranker)
    {
        var monitor = Substitute.For<IOptionsMonitor<MemoryMcpOptions>>();
        monitor.CurrentValue.Returns(new MemoryMcpOptions());
        return new MemorySearchTool(this.search, Substitute.For<IEmbeddingService>(), monitor, reranker).ExecuteAsync(json, Context, CancellationToken.None);
    }

    [Fact]
    public async Task Hybrid_RerankOn_AsksForTopNCandidates_ReordersAndCutsToLimit()
    {
        this.Returns(MemorySearchMode.Hybrid, Hit("m1", "coffee"), Hit("m2", "budget"), Hit("m3", "night train to TownB", "Trip"));

        var result = await this.RunAsync("""{"query":"night train","mode":"hybrid","limit":2}""", this.stage);

        await this.search.Received(1).SearchAsync(Arg.Is<HybridSearchRequest>(r => r.Limit == 3), Arg.Any<CancellationToken>());
        Assert.StartsWith("Found 2 matching memories", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.IndexOf("m3", StringComparison.Ordinal) < result.Content.IndexOf("m1", StringComparison.Ordinal));
        Assert.DoesNotContain("m2", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("rerank: on (top 3 reordered, ≤1200 chars of matching lines each)", result.Content, StringComparison.Ordinal);
        Assert.Equal(["Trip\nnight train to TownB", "coffee", "budget"], this.reranker.Calls[0].Documents.OrderByDescending(d => d.Length));
    }

    [Fact]
    public async Task DenseMode_NotReranked_OutputUnchanged()
    {
        this.Returns(MemorySearchMode.Dense, Hit("m1", "coffee"), Hit("m2", "budget"));

        var reranked = await this.RunAsync("""{"query":"night train","mode":"dense"}""", this.stage);
        var plain = await this.RunAsync("""{"query":"night train","mode":"dense"}""", null);

        Assert.Empty(this.reranker.Calls);
        Assert.Equal(plain.Content, reranked.Content);
    }

    [Fact]
    public async Task HybridFellBackToKeyword_CutToLimit_AndSaysRerankWasNotUsed()
    {
        this.Returns(MemorySearchMode.Keyword, Hit("m1", "coffee"), Hit("m2", "budget"), Hit("m3", "train"));

        var result = await this.RunAsync("""{"query":"train","mode":"hybrid","limit":1}""", this.stage);

        Assert.Empty(this.reranker.Calls);
        Assert.StartsWith("Found 1 matching memories", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("rerank: not used: mode=keyword (reranking follows hybrid fusion)", result.Content, StringComparison.Ordinal);
    }
}
