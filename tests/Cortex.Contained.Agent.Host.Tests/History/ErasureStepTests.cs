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
        var runs = Path.Combine(this.dir, "smoke");
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
        monitor.CurrentValue.Returns(new HistoryOptions { SmokeArtifactsDirectory = runs });

        Assert.True(await new SmokeArtifactErasureStep(monitor).RunAsync(Task(T0, T0.AddDays(1)), CancellationToken.None));

        Assert.False(Directory.Exists(overlapping));
        Assert.False(Directory.Exists(noManifest));
        Assert.True(Directory.Exists(disjoint));
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
}
