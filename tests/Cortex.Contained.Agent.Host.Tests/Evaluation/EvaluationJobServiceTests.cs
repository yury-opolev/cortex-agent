using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation;

public sealed class EvaluationJobServiceTests : IDisposable
{
    private readonly TaskCompletionSource<int> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<EvaluationRun> runs = [];
    private readonly EvaluationJobService service;

    public EvaluationJobServiceTests()
    {
        var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
        {
            ["history-eval"] = async (run, ct) =>
            {
                this.runs.Add(run);
                await run.Output.WriteLineAsync("Report: /app/state/eval/hybrid/runs/x/report.md");
                return await this.gate.Task.WaitAsync(ct);
            },
        };
        this.service = new EvaluationJobService(commands, () => EvalProtection.Encrypted(EvalTestKeys.Ring()), TimeProvider.System, NullLogger<EvaluationJobService>.Instance);
    }

    public void Dispose() => this.service.Dispose();

    [Fact]
    public async Task Start_RunsInTheBackgroundEncryptedAndKeepsTheOutput()
    {
        var started = this.service.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["ablate", "--split", "dev"], Passphrase = "zqpassphrase-1234" });

        Assert.Equal(EvaluationJobStates.Running, started.State);
        Assert.True(started.Encrypted);
        Assert.Equal("ablate", started.Command);
        this.gate.SetResult(0);
        await this.service.WhenIdleAsync();

        var done = this.service.Get(started.Id)!;
        Assert.Equal(EvaluationJobStates.Succeeded, done.State);
        Assert.Equal(0, done.ExitCode);
        Assert.Contains("report.md", done.Output, StringComparison.Ordinal);
        Assert.True(Assert.Single(this.runs).Protection.IsEncrypted);
        Assert.Equal("zqpassphrase-1234", this.runs[0].Passphrase);
        Assert.DoesNotContain("zqpassphrase", System.Text.Json.JsonSerializer.Serialize(done), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_WhileAJobRuns_RefusedThenCancelEndsTheRunningJob()
    {
        var first = this.service.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["holdout"] });

        var second = this.service.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["ablate"] });
        Assert.Equal(EvaluationJobStates.Refused, second.State);
        Assert.Contains("Another evaluation job", second.Output, StringComparison.Ordinal);

        Assert.True(this.service.Cancel(first.Id));
        await this.service.WhenIdleAsync();
        Assert.Equal(EvaluationJobStates.Cancelled, this.service.Get(first.Id)!.State);
        Assert.False(this.service.Cancel(first.Id));
    }

    [Theory]
    [InlineData("--dir")]
    [InlineData("--source")]
    [InlineData("--embedder")]
    public void Start_PathOrEndpointOption_Refused(string option)
    {
        var job = this.service.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["ablate", option, "/tmp/x"] });

        Assert.Equal(EvaluationJobStates.Refused, job.State);
        Assert.Empty(this.runs);
    }

    [Fact]
    public void Start_UnknownToolOrLockedStorage_Refused()
    {
        Assert.Equal(EvaluationJobStates.Refused, this.service.Start(new EvaluationStartRequestDto { Tool = "history-smoke", Args = ["run"] }).State);

        using var locked = new EvaluationJobService(
            new Dictionary<string, EvaluationCommand> { ["history-eval"] = (_, _) => Task.FromResult(0) },
            () => EvalProtection.Encrypted(new StorageKeyRing()),
            TimeProvider.System,
            NullLogger<EvaluationJobService>.Instance);
        var job = locked.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["ablate"] });

        Assert.Equal(EvaluationJobStates.Refused, job.State);
        Assert.Contains("storage key", job.Output, StringComparison.Ordinal);
    }
}
