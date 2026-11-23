using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.History;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.History;

public sealed class ErasureStepTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"erasure-steps-{Guid.NewGuid():N}");

    public ErasureStepTests()
    {
        Directory.CreateDirectory(this.dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    private static ErasureTask Task(DateTimeOffset? from, DateTimeOffset? to, string? channel = null) =>
        new() { Id = "t", CreatedAt = T0, RangeFrom = from, RangeTo = to, ChannelId = channel, Steps = ["x"] };

    [Fact]
    public async Task ToolOutput_DeletesEverySpillFile()
    {
        var output = Path.Combine(this.dir, "tool-output");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "a.txt"), "spill");
        await File.WriteAllTextAsync(Path.Combine(output, "b.txt"), "spill");

        Assert.True(await new ToolOutputErasureStep(output).RunAsync(Task(T0, T0), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(output));
        Assert.True(await new ToolOutputErasureStep(Path.Combine(this.dir, "missing")).RunAsync(Task(T0, T0), CancellationToken.None));
    }

    [Fact]
    public async Task SmokeArtifacts_DestroysOverlappingAndUnreadableRuns()
    {
        var root = Path.Combine(this.dir, "smoke");
        var runs = Path.Combine(root, "runs");
        var overlapping = Path.Combine(runs, "run-1");
        var disjoint = Path.Combine(runs, "run-2");
        var noManifest = Path.Combine(runs, "run-3");
        foreach (var run in new[] { overlapping, disjoint, noManifest })
        {
            Directory.CreateDirectory(run);
        }

        await File.WriteAllTextAsync(Path.Combine(overlapping, "manifest.json"), """{"corpusFrom":"2026-05-01T00:00:00Z","corpusTo":"2026-05-10T00:00:00Z"}""");
        await File.WriteAllTextAsync(Path.Combine(disjoint, "manifest.json"), """{"corpusFrom":"2026-06-01T00:00:00Z","corpusTo":"2026-06-10T00:00:00Z"}""");
        var monitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
        monitor.CurrentValue.Returns(new HistoryOptions { SmokeArtifactsDirectory = root, EvalArtifactsDirectory = Path.Combine(this.dir, "none") });

        Assert.True(await new SmokeArtifactErasureStep(monitor).RunAsync(Task(T0, T0.AddDays(1)), CancellationToken.None));

        Assert.False(Directory.Exists(overlapping));
        Assert.False(Directory.Exists(noManifest));
        Assert.True(Directory.Exists(disjoint));
    }

    [Fact]
    public async Task EvaluationArtifacts_DestroysOverlappingRunsInBothWorkspacesAndKeepsOwnerFiles()
    {
        var smoke = Path.Combine(this.dir, "smoke");
        var eval = Path.Combine(this.dir, "eval");
        var evalOverlap = Path.Combine(eval, "runs", "ablate-dev-1");
        var evalDisjoint = Path.Combine(eval, "runs", "holdout-test-1");
        var evalUnreadable = Path.Combine(eval, "runs", "guard-1");
        var smokeOverlap = Path.Combine(smoke, "runs", "run-1");
        var pool = Path.Combine(eval, "pool");
        foreach (var d in new[] { evalOverlap, evalDisjoint, evalUnreadable, smokeOverlap, pool })
        {
            Directory.CreateDirectory(d);
        }

        const string Overlapping = """{"corpusFrom":"2026-05-01T00:00:00Z","corpusTo":"2026-05-10T00:00:00Z"}""";
        await File.WriteAllTextAsync(Path.Combine(evalOverlap, "manifest.json"), Overlapping);
        await File.WriteAllTextAsync(Path.Combine(evalOverlap, "messages.db"), "raw copy");
        await File.WriteAllTextAsync(Path.Combine(smokeOverlap, "manifest.json"), Overlapping);
        await File.WriteAllTextAsync(Path.Combine(evalDisjoint, "manifest.json"), """{"corpusFrom":"2026-07-01T00:00:00Z","corpusTo":"2026-07-10T00:00:00Z"}""");
        await File.WriteAllTextAsync(Path.Combine(evalUnreadable, "manifest.json"), "not json");
        await File.WriteAllTextAsync(Path.Combine(eval, "dataset.jsonl"), "owner labels");
        await File.WriteAllTextAsync(Path.Combine(pool, "keep.txt"), "owner file");
        var monitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
        monitor.CurrentValue.Returns(new HistoryOptions { SmokeArtifactsDirectory = smoke, EvalArtifactsDirectory = eval });

        Assert.True(await new SmokeArtifactErasureStep(monitor).RunAsync(Task(T0, T0.AddDays(1)), CancellationToken.None));

        Assert.False(Directory.Exists(evalOverlap));
        Assert.False(Directory.Exists(evalUnreadable));
        Assert.False(Directory.Exists(smokeOverlap));
        Assert.True(Directory.Exists(evalDisjoint));
        Assert.True(File.Exists(Path.Combine(eval, "dataset.jsonl")));
        Assert.True(File.Exists(Path.Combine(pool, "keep.txt")));
    }

    [Fact]
    public async Task FrozenCorpora_OverlappingVersionsDestroyedExportedOnesQueuedForOffsiteDeletion()
    {
        var store = new Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore(Path.Combine(this.dir, "state-eval"));
        void Version(string name, DateTimeOffset from, DateTimeOffset to, bool exported)
        {
            Directory.CreateDirectory(store.DirectoryOf(name));
            new Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusManifest { Version = name, CorpusFrom = from, CorpusTo = to, ExportedAt = exported ? T0 : null }
                .Write(Path.Combine(store.DirectoryOf(name), "manifest.json"));
        }

        Version("v1", T0.AddDays(-10), T0.AddHours(2), exported: true);
        Version("v2", T0.AddDays(-10), T0.AddHours(2), exported: false);
        Version("v3", T0.AddDays(5), T0.AddDays(9), exported: true);
        Directory.CreateDirectory(store.DirectoryOf(store.ReserveNextVersion()) + ".partial");
        Directory.CreateDirectory(store.PackagesDirectory);
        await File.WriteAllTextAsync(store.PackageOf("v1"), "package");
        var monitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
        monitor.CurrentValue.Returns(new HistoryOptions { SmokeArtifactsDirectory = Path.Combine(this.dir, "none"), EvalArtifactsDirectory = Path.Combine(this.dir, "none") });

        Assert.True(await new SmokeArtifactErasureStep(monitor, store).RunAsync(Task(T0, T0.AddDays(1)), CancellationToken.None));

        Assert.Equal(["v3"], store.List().Select(m => m.Version));
        Assert.False(Directory.Exists(store.DirectoryOf("v4") + ".partial"));
        Assert.False(File.Exists(store.PackageOf("v1")));
        var pending = Assert.Single(store.PendingDeletions());
        Assert.Equal(("v1", "cortex-recovery-kit", "cortex-eval-corpus-v1.cxpkg"), (pending.Version, pending.Secret, pending.PackageFileName));
        Assert.Equal("v5", store.ReserveNextVersion());
        var purge = Assert.Single(store.CompletedPurges());
        Assert.Equal((T0, T0.AddDays(1)), (purge.From, purge.To));
    }

    [Fact]
    public async Task SmokeArtifacts_RootWithoutRunsDirectory_DeletesNothing()
    {
        var root = Path.Combine(this.dir, "smoke");
        var other = Path.Combine(root, "pool");
        Directory.CreateDirectory(other);
        var monitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
        monitor.CurrentValue.Returns(new HistoryOptions { SmokeArtifactsDirectory = root, EvalArtifactsDirectory = Path.Combine(this.dir, "none") });

        Assert.True(await new SmokeArtifactErasureStep(monitor).RunAsync(Task(T0, T0.AddDays(1)), CancellationToken.None));

        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void PurgeOverlapping_DeletesTerminalCountsActiveAndHonoursChannel()
    {
        using var store = new SubagentSessionStore(this.dir, NullLogger<SubagentSessionStore>.Instance);
        SubagentTask Make(string id, SubagentTaskState state, string channel, DateTimeOffset created, DateTimeOffset? completed) => new()
        {
            TaskId = id, ParentConversation = "c", ParentChannel = channel, Description = "d", Prompt = "p",
            State = state, CreatedAt = created, CompletedAt = completed,
        };
        store.Create(Make("done", SubagentTaskState.Completed, "ch", T0.AddMinutes(-5), T0.AddMinutes(5)));
        store.Create(Make("other-channel", SubagentTaskState.Completed, "other", T0, T0.AddMinutes(1)));
        store.Create(Make("running", SubagentTaskState.Running, "ch", T0, null));
        store.Create(Make("before", SubagentTaskState.Completed, "ch", T0.AddDays(-9), T0.AddDays(-8)));

        var (deleted, active) = store.PurgeOverlapping(T0, T0.AddHours(1), "ch");

        Assert.Equal((1, 1), (deleted, active));
        Assert.Null(store.GetById("done"));
        Assert.NotNull(store.GetById("other-channel"));
        Assert.NotNull(store.GetById("before"));
    }

    [Fact]
    public async Task Subagents_ChannelPurge_DeletesRecallUsersOfEveryParentChannel()
    {
        using var store = new SubagentSessionStore(this.dir, NullLogger<SubagentSessionStore>.Instance);
        SubagentTask Make(string id, SubagentTaskState state, string channel, string? tool, DateTimeOffset created, DateTimeOffset? completed) => new()
        {
            TaskId = id, ParentConversation = channel, ParentChannel = channel, Description = "d", Prompt = "p",
            State = state, CreatedAt = created, CompletedAt = completed,
            Messages = tool is null
                ? [new Cortex.Contained.Contracts.Llm.LlmMessage { Role = "user", Content = "history_search is mentioned here only as text" }]
                : [new Cortex.Contained.Contracts.Llm.LlmMessage
                {
                    Role = "assistant",
                    ToolCalls = [new Cortex.Contained.Contracts.Llm.LlmToolCall { Id = "c1", Name = tool, Arguments = "{}" }],
                }],
        };
        store.Create(Make("recall-other", SubagentTaskState.Completed, "other", "history_read", T0, T0.AddMinutes(1)));
        store.Create(Make("memory-other", SubagentTaskState.Completed, "other", "memory_search", T0, T0.AddMinutes(1)));
        store.Create(Make("no-recall-other", SubagentTaskState.Completed, "other", null, T0, T0.AddMinutes(1)));
        store.Create(Make("recall-later", SubagentTaskState.Completed, "other", "event_get", T0.AddDays(3), T0.AddDays(3)));
        store.Create(Make("recall-running", SubagentTaskState.Running, "other", "history_search", T0, null));

        var done = await new SubagentErasureStep(store).RunAsync(Task(T0, T0.AddHours(1), "purged"), CancellationToken.None);

        Assert.False(done);
        Assert.Null(store.GetById("recall-other"));
        Assert.Null(store.GetById("memory-other"));
        Assert.NotNull(store.GetById("no-recall-other"));
        Assert.NotNull(store.GetById("recall-later"));
        Assert.NotNull(store.GetById("recall-running"));
    }

    [Fact]
    public async Task Subagents_ChannelPurge_DeletesTerminalTasksWhoseMessagesWereAlreadyCleared()
    {
        using var store = new SubagentSessionStore(this.dir, NullLogger<SubagentSessionStore>.Instance);
        store.Create(new SubagentTask
        {
            TaskId = "cleared", ParentConversation = "other", ParentChannel = "other", Description = "d", Prompt = "p",
            State = SubagentTaskState.Completed, CreatedAt = T0, CompletedAt = T0.AddMinutes(1),
        });

        Assert.True(await new SubagentErasureStep(store).RunAsync(Task(T0, T0.AddHours(1), "purged"), CancellationToken.None));

        Assert.Null(store.GetById("cleared"));
    }
}