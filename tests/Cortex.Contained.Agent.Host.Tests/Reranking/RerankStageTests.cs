using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The reranking stage: budget, honest notes, timeout, circuit breaker, health.</summary>
public sealed class RerankStageTests
{
    private static readonly RerankBudget Budget = new(3, 600);
    private readonly FakeReranker reranker = new();
    private readonly RerankerOptions options = new() { TimeoutMs = 200, FailureThreshold = 2, BreakSeconds = 60 };
    private readonly RerankerAccess access = new();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly RerankStage stage;

    public RerankStageTests()
    {
        var monitor = Substitute.For<IOptionsMonitor<RerankerOptions>>();
        monitor.CurrentValue.Returns(this.options);
        this.access.Apply(new RerankerGrant { Enabled = true, ApiKey = "k" });
        this.stage = new RerankStage(this.reranker, monitor, this.access, NullLogger<RerankStage>.Instance, this.time);
    }

    private static List<IReadOnlyList<string>> Candidates(params string[] texts) => texts.Select(t => (IReadOnlyList<string>)[t]).ToList();

    [Fact]
    public async Task RerankAsync_ReordersOnlyTheTopN_RestKeepsFusedOrder()
    {
        var outcome = await this.stage.RerankAsync("train", Candidates("coffee", "budget", "train times", "train again"), Budget, CancellationToken.None);

        Assert.Equal(RerankStatus.Applied, outcome.Status);
        Assert.Equal([2, 0, 1, 3], outcome.Order);
        Assert.Equal(3, this.reranker.Calls[0].Documents.Count);
        Assert.StartsWith("on (top 3 reordered", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void BudgetFor_OffUntilTheBridgeEnablesIt_AndPerSurface()
    {
        this.access.Apply(new RerankerGrant { Enabled = false });
        Assert.Null(this.stage.BudgetFor(RerankSurface.History));

        this.access.Apply(new RerankerGrant { Enabled = true, ApiKey = "k" });
        this.options.Memory.Enabled = false;

        Assert.Equal(new RerankBudget(15, 1200), this.stage.BudgetFor(RerankSurface.History));
        Assert.Null(this.stage.BudgetFor(RerankSurface.Memory));
    }

    [Fact]
    public async Task RerankAsync_Timeout_KeepsFusedOrderAndSaysSo()
    {
        this.reranker.Delay = TimeSpan.FromSeconds(30);

        var outcome = await this.stage.RerankAsync("train", Candidates("a", "b", "c"), Budget, CancellationToken.None);

        Assert.Equal(RerankStatus.Failed, outcome.Status);
        Assert.Equal([0, 1, 2], outcome.Order);
        Assert.Equal("failed: timed out after 200 ms; fused order kept", outcome.Note);
    }

    [Fact]
    public async Task RerankAsync_RepeatedFailures_OpenTheCircuit_ThenSkipWithoutCalling()
    {
        this.reranker.Failure = new RerankerException("The reranker answered HTTP 500.");
        await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);
        await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);

        var outcome = await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);

        Assert.Equal(RerankStatus.Skipped, outcome.Status);
        Assert.StartsWith("skipped: circuit open after 2 failures", outcome.Note, StringComparison.Ordinal);
        Assert.Equal(2, this.reranker.Calls.Count);
        Assert.Equal("circuit-open", this.stage.Snapshot().State);
        Assert.Equal("The reranker answered HTTP 500", this.stage.Snapshot().LastError);
    }

    [Fact]
    public async Task RerankAsync_CancelledHalfOpenTrial_ReleasesTheTrialSoTheNextCallCanTryAgain()
    {
        this.reranker.Failure = new RerankerException("The reranker answered HTTP 500.");
        await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);
        await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);
        this.time.Advance(TimeSpan.FromSeconds(61));
        this.reranker.Failure = null;
        this.reranker.Delay = TimeSpan.FromSeconds(30);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => this.stage.RerankAsync("q", Candidates("a", "b"), Budget, cancel.Token));
        this.reranker.Delay = null;
        var next = await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);

        Assert.Equal(RerankStatus.Applied, next.Status);
        Assert.Equal("ok", this.stage.Snapshot().State);
    }

    [Fact]
    public async Task Snapshot_AfterTheBreakEndsUntilATrialRuns_IsNotOk()
    {
        this.reranker.Failure = new RerankerException("The reranker answered HTTP 500.");
        await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);
        await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);
        this.time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal("half-open", this.stage.Snapshot().State);
    }

    [Fact]
    public async Task RerankAsync_Unavailable_SkipsWithReason()
    {
        this.reranker.UnavailableReason = "no reranker API key";

        var outcome = await this.stage.RerankAsync("q", Candidates("a", "b"), Budget, CancellationToken.None);

        Assert.Equal("skipped: no reranker API key", outcome.Note);
        Assert.Empty(this.reranker.Calls);
    }

    [Fact]
    public async Task RerankAsync_SingleCandidate_NotSent()
    {
        var outcome = await this.stage.RerankAsync("q", Candidates("a"), Budget, CancellationToken.None);

        Assert.Equal(RerankStatus.Skipped, outcome.Status);
        Assert.Empty(this.reranker.Calls);
    }
}
