using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation.Smoke;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Smoke-evaluation harness (§14.2) on a synthetic corpus.</summary>
public sealed class HistorySmokeCliTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"smoke-{Guid.NewGuid():N}");
    private readonly SmokeWorkspace workspace;
    private readonly string source;

    public HistorySmokeCliTests()
    {
        this.workspace = new SmokeWorkspace(Path.Combine(this.root, "smoke"));
        Directory.CreateDirectory(this.workspace.Directory);
        this.source = Path.Combine(this.root, "messages.db");
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private async Task<(long Train, long Ferry)> SeedAsync()
    {
        await using var store = new MessageStore(this.source, NullLogger<MessageStore>.Instance);
        var train = await store.SaveMessageAsync("u", "ch", "user", "the night train to TownB leaves at nine zqsecret", T0);
        await store.SaveMessageAsync("a", "ch", "assistant", "noted", T0.AddMinutes(1));
        var ferry = await store.SaveMessageAsync("u", "ch", "user", "ferries to TownC on Sundays", T0.AddDays(1));
        return (train, ferry);
    }

    private void WriteQueries(long train, long ferry)
    {
        File.WriteAllLines(this.workspace.QueriesPath,
        [
            $$"""{"id":"q01","request":"when does the train to TownB leave?","oracle":{"query":"train townb"},"gold":[{{train}}]}""",
            $$"""{"id":"q02","request":"what about ferries?","oracle":{"query":"ferries"},"gold":[{{ferry}}]}""",
        ]);
        // q01: OR-joined FTS finds the train although "tomorrow" never occurs; the AND-ed LIKE baseline misses (a win).
        // q02: both systems find the ferry message (a tie).
        File.WriteAllLines(this.workspace.ModelQueriesPath,
        [
            """{"id":"q01","spec":{"query":"train TownB tomorrow"}}""",
            """{"id":"q02","spec":{"query":"ferries"}}""",
        ]);
        File.WriteAllText(this.workspace.AnalysisPlanPath, "# plan");
    }

    [Fact]
    public async Task RunEvaluationAsync_Unregistered_Refuses()
    {
        var (train, ferry) = await this.SeedAsync();
        this.WriteQueries(train, ferry);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HistorySmokeCli.RunEvaluationAsync(this.workspace, this.source, new HistoryOptions { Enabled = true, TimeZone = "UTC" }, CancellationToken.None));
    }

    [Fact]
    public async Task RunEvaluationAsync_QueriesChangedAfterRegister_Refuses()
    {
        var (train, ferry) = await this.SeedAsync();
        this.WriteQueries(train, ferry);
        this.workspace.Register();
        File.AppendAllText(this.workspace.AnalysisPlanPath, "\nchanged after seeing data");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HistorySmokeCli.RunEvaluationAsync(this.workspace, this.source, new HistoryOptions { Enabled = true, TimeZone = "UTC" }, CancellationToken.None));
        Assert.Contains("analysis-plan.md", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunEvaluationAsync_Registered_ScoresAllSystemsWithoutWritingMessageText()
    {
        var (train, ferry) = await this.SeedAsync();
        this.WriteQueries(train, ferry);
        this.workspace.Register();

        var (runDir, gate) = await HistorySmokeCli.RunEvaluationAsync(
            this.workspace, this.source, new HistoryOptions { Enabled = true, TimeZone = "UTC" }, CancellationToken.None);

        var results = await File.ReadAllTextAsync(Path.Combine(runDir, "results.json"));
        var report = await File.ReadAllTextAsync(Path.Combine(runDir, "report.md"));
        Assert.Contains("\"system\": \"b-model\"", results, StringComparison.Ordinal);
        Assert.DoesNotContain("zqsecret", results, StringComparison.Ordinal);
        Assert.DoesNotContain("zqsecret", report, StringComparison.Ordinal);
        Assert.Contains("Gate: (b) vs (c)", report, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(runDir, "manifest.json")));
        Assert.Equal(1, gate.Wins);
        Assert.Equal(1, gate.Ties);
    }

    [Fact]
    public void Gate_WinsDominate_Passes()
    {
        var rows = Enumerable.Range(0, 10).SelectMany(i => new[]
        {
            new SmokeScoreRow($"q{i}", SmokeReport.Model, 1500, true, true, 1, 10),
            new SmokeScoreRow($"q{i}", SmokeReport.Like, 1500, false, false, null, 10),
        }).ToList();

        var gate = SmokeReport.Gate(rows);

        Assert.True(gate.Passed);
        Assert.Equal(10, gate.Wins);
        Assert.Empty(gate.LossQueryIds);
    }

    [Fact]
    public async Task SmokeArtifactErasureStep_RunOverlappingPurgedRange_IsDestroyed()
    {
        var (train, ferry) = await this.SeedAsync();
        this.WriteQueries(train, ferry);
        this.workspace.Register();
        var (runDir, _) = await HistorySmokeCli.RunEvaluationAsync(
            this.workspace, this.source, new HistoryOptions { Enabled = true, TimeZone = "UTC" }, CancellationToken.None);
        SqliteConnectionPools.Clear();
        var monitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<HistoryOptions>>();
        monitor.CurrentValue.Returns(new HistoryOptions { SmokeArtifactsDirectory = this.workspace.RunsDirectory });

        var done = await new SmokeArtifactErasureStep(monitor).RunAsync(
            new ErasureTask { Id = "t", CreatedAt = T0, RangeFrom = T0, RangeTo = T0, Steps = [ErasureSteps.SmokeArtifacts] },
            CancellationToken.None);

        Assert.True(done);
        Assert.False(Directory.Exists(runDir));
        Assert.True(File.Exists(this.workspace.QueriesPath));
    }

    private static class SqliteConnectionPools
    {
        public static void Clear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
