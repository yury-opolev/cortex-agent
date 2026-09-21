using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Agent.Autonomy;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// The behaviour that makes a subagent autonomous: a finished loop is no longer the end of the
/// run. Without these, the supervisor is inert.
/// </summary>
public sealed class SubagentRunnerGoalTests
{
    [Fact]
    public async Task RunAsync_NoSupervisor_RunsExactlyOneLoop()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream("all done"));
        using var runner = NewRunner(llm);

        var result = await runner.RunAsync("m", "sys", "prompt", "conv-1", CancellationToken.None);

        Assert.Equal(SubagentTaskState.Completed, result.TerminalState);
        llm.Received(1).StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_JudgeSaysContinueThenDone_RunsASecondLoop()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream("did a bit"));

        // The judge is a non-streaming call, so it is mocked independently of the loop itself.
        var judgeReplies = new Queue<string>(["CONTINUE: the tests still fail", "DONE"]);
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new LlmCompletionResult { Success = true, Content = judgeReplies.Dequeue() }));

        using var runner = NewRunner(llm);
        runner.SetSupervisor(Supervisor(llm));

        var result = await runner.RunAsync("m", "sys", "prompt", "conv-1", CancellationToken.None);

        Assert.Equal(SubagentTaskState.Completed, result.TerminalState);

        // Two loops, because the first CONTINUE fed the judge's remaining text back in.
        llm.Received(2).StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());
        Assert.Empty(judgeReplies);
    }

    [Fact]
    public async Task RunAsync_JudgeSaysDoneImmediately_DoesNotLoopAgain()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream("finished"));
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmCompletionResult { Success = true, Content = "DONE" }));

        using var runner = NewRunner(llm);
        runner.SetSupervisor(Supervisor(llm));

        var result = await runner.RunAsync("m", "sys", "prompt", "conv-1", CancellationToken.None);

        Assert.Equal(SubagentTaskState.Completed, result.TerminalState);
        llm.Received(1).StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_GoalRunEnds_ReportIsTheResultText()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream("finished"));
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmCompletionResult { Success = true, Content = "DONE" }));

        using var runner = NewRunner(llm);
        var supervisor = Supervisor(llm);
        supervisor.Ledger.RecordAssumption("cache backend", "used in-memory", "redis was unreachable");
        runner.SetSupervisor(supervisor);

        var result = await runner.RunAsync("m", "sys", "prompt", "conv-1", CancellationToken.None);

        // The run reports its ledger, not just its last sentence.
        Assert.Contains("used in-memory", result.Result, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static SubagentRunner NewRunner(ILlmClient llm)
        => new(llm, new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance), 4, NullLogger<SubagentRunner>.Instance);

    private static AutonomySupervisor Supervisor(ILlmClient llm)
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.DefaultModel.Returns("m");

        return new AutonomySupervisor(
            "make it green",
            GoalBudget.Create(TimeProvider.System),
            new CompletionJudge(llm, modelProvider, NullLogger<CompletionJudge>.Instance),
            new StuckDetector(),
            new AssumptionLedger(),
            NullLogger.Instance);
    }

    private static async IAsyncEnumerable<LlmStreamChunk> Stream(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ContentDelta = content, IsComplete = true, FinishReason = "stop" };
    }
}
