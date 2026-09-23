using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Agent.Autonomy;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// The mid-loop gate. These are the tests that prove the protection is not dead code: the
/// completion judge is only reached when a turn calls NO tools, so an agent that calls one every
/// round must still be bounded, and that can only happen inside the loop.
/// </summary>
public sealed class MidLoopGateTests
{
    [Fact]
    public async Task OnRoundCompleteAsync_NoSupervisor_KeepsLooping()
    {
        var callbacks = NewCallbacks(out _);

        Assert.True(await callbacks.OnRoundCompleteAsync(1, null, CancellationToken.None));
    }

    [Fact]
    public async Task OnRoundCompleteAsync_BudgetExhausted_HaltsTheLoop()
    {
        var callbacks = NewCallbacks(out var session);
        var supervisor = Supervisor(GoalBudget.Rehydrate(
            new GoalBudgetConsumed(TimeSpan.Zero, GoalBudget.DefaultContinuationLimit),
            TimeProvider.System));
        callbacks.SetSupervisor(supervisor);

        var keepGoing = await callbacks.OnRoundCompleteAsync(1, null, CancellationToken.None);

        Assert.False(keepGoing);
        Assert.Equal(GoalOutcome.BudgetExhausted, supervisor.MidLoopStop);
        _ = session;
    }

    [Fact]
    public async Task OnRoundCompleteAsync_RepeatedIdenticalAction_NudgesThenHalts()
    {
        var callbacks = NewCallbacks(out var session);
        var supervisor = Supervisor(GoalBudget.Create(TimeProvider.System));
        callbacks.SetSupervisor(supervisor);

        var call = new LlmToolCall { Id = "1", Name = "grep", Arguments = "{\"q\":\"same\"}" };
        var result = new AgentToolResult { Success = true, Content = "no matches" };

        // Drive identical action+observation rounds until the gate reacts.
        var halted = false;
        var nudged = false;
        for (var i = 0; i < 12 && !halted; i++)
        {
            await callbacks.OnToolCompleteAsync(call, result, TimeSpan.Zero, CancellationToken.None);
            var keepGoing = await callbacks.OnRoundCompleteAsync(i + 1, null, CancellationToken.None);

            if (!nudged && session.DrainPendingMessages().Any(m => m.Text.Contains("autonomy supervisor", StringComparison.Ordinal)))
            {
                nudged = true;
            }

            halted = !keepGoing;
        }

        // Nudged first (a run that can see its loop often breaks out), then halted if it persisted.
        Assert.True(nudged, "the gate should nudge before halting");
        Assert.True(halted, "a persistent loop should eventually halt the run");
        Assert.Equal(GoalOutcome.Stalled, supervisor.MidLoopStop);
    }

    [Fact]
    public async Task OnRoundCompleteAsync_VaryingActions_DoesNotHalt()
    {
        var callbacks = NewCallbacks(out _);
        callbacks.SetSupervisor(Supervisor(GoalBudget.Create(TimeProvider.System)));

        for (var i = 0; i < 10; i++)
        {
            await callbacks.OnToolCompleteAsync(
                new LlmToolCall { Id = $"{i}", Name = "read_file", Arguments = $"{{\"path\":\"f{i}.txt\"}}" },
                new AgentToolResult { Success = true, Content = $"contents {i}" },
                TimeSpan.Zero,
                CancellationToken.None);

            Assert.True(await callbacks.OnRoundCompleteAsync(i + 1, null, CancellationToken.None));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static SubagentCallbacks NewCallbacks(out AgentSession pendingSession)
    {
        pendingSession = new AgentSession("subagent-pending");
        return new SubagentCallbacks(
            [],
            contextWindow: 128_000,
            maxOutputTokens: 4096,
            conversationId: "subagent-t1",
            Substitute.For<ILlmClient>(),
            NullLogger.Instance,
            todoStore: null,
            store: null,
            taskId: null,
            pendingSession,
            imageAging: null,
            imageDescriber: null);
    }

    private static AutonomySupervisor Supervisor(GoalBudget budget)
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.DefaultModel.Returns("m");

        return new AutonomySupervisor(
            "make it green",
            budget,
            new CompletionJudge(Substitute.For<ILlmClient>(), modelProvider, NullLogger<CompletionJudge>.Instance),
            new StuckDetector(),
            new AssumptionLedger(),
            NullLogger.Instance);
    }
}
