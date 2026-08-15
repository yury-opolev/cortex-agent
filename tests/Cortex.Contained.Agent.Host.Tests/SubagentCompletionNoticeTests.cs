using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// The synthetic parent-turn instruction built for a terminal subagent task.
/// <para>
/// Root cause this locks down (2026-08-15): the notification text was state-blind — a task
/// that died with "Stream transport fault: LLM stream produced no data for 120s." was announced
/// to the parent as "[Background task completed]" with the error pasted in as the Result. A
/// Failed, Cancelled and Completed task were lexically indistinguishable, so the orchestrator
/// treated 25 minutes of lost research as a success and never resumed it — even though the full
/// message history had been checkpointed and <c>sub_agent_send</c> could have resumed it.
/// </para>
/// </summary>
public class SubagentCompletionNoticeTests
{
    private static SubagentTask Task(SubagentTaskState state, string? result = "the result") => new()
    {
        TaskId = "sa-abc123",
        ParentConversation = "discord-dm",
        ParentChannel = "discord",
        Description = "Deep research on SERV",
        Prompt = "research it",
        State = state,
        Result = result,
    };

    // ── Completed: the pre-existing happy path must not regress ────────

    [Fact]
    public void Build_CompletedTask_KeepsTheCompletedEnvelopeAndTheResult()
    {
        var text = SubagentCompletionNotice.Build(Task(SubagentTaskState.Completed));

        Assert.Contains("[Background task completed]", text, StringComparison.Ordinal);
        Assert.Contains("the result", text, StringComparison.Ordinal);
        Assert.Contains("sa-abc123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FAILED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("did NOT finish", text, StringComparison.Ordinal);
    }

    // ── Failed: the actual bug ─────────────────────────────────────────

    [Fact]
    public void Build_FailedTask_IsNotAnnouncedAsCompleted()
    {
        var text = SubagentCompletionNotice.Build(
            Task(SubagentTaskState.Failed, "Stream transport fault: LLM stream produced no data for 120s."));

        Assert.DoesNotContain("[Background task completed]", text, StringComparison.Ordinal);
        Assert.Contains("[Background task FAILED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailedTask_SurfacesTheErrorAsAFailureNotAsAResult()
    {
        var text = SubagentCompletionNotice.Build(
            Task(SubagentTaskState.Failed, "Stream transport fault: LLM stream produced no data for 120s."));

        Assert.Contains("Stream transport fault", text, StringComparison.Ordinal);
        Assert.Contains("Failure:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Result:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailedTask_TellsTheParentToResumeRatherThanRestart()
    {
        var text = SubagentCompletionNotice.Build(Task(SubagentTaskState.Failed, "boom"));

        // The work is preserved (history is checkpointed every round) and sub_agent_send can
        // resume a Failed task — the parent must be told so, by task id.
        Assert.Contains("sub_agent_send", text, StringComparison.Ordinal);
        Assert.Contains("sa-abc123", text, StringComparison.Ordinal);
        Assert.Contains("preserved", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sub_agent_start", text, StringComparison.Ordinal);
    }

    // ── Cancelled ──────────────────────────────────────────────────────

    [Fact]
    public void Build_CancelledTask_IsAnnouncedAsCancelledNotCompleted()
    {
        var text = SubagentCompletionNotice.Build(
            Task(SubagentTaskState.Cancelled, "[Subagent stopped]"));

        Assert.Contains("[Background task cancelled]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[Background task completed]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[Background task FAILED]", text, StringComparison.Ordinal);
    }

    // ── Every state is covered, including non-terminal ones ────────────

    [Theory]
    [InlineData(SubagentTaskState.Queued)]
    [InlineData(SubagentTaskState.Running)]
    [InlineData(SubagentTaskState.Revising)]
    [InlineData(SubagentTaskState.Completed)]
    [InlineData(SubagentTaskState.Failed)]
    [InlineData(SubagentTaskState.Cancelled)]
    public void Build_AnyState_ProducesAnEnvelopeThatNamesTheTask(SubagentTaskState state)
    {
        var text = SubagentCompletionNotice.Build(Task(state));

        Assert.StartsWith("[Background task ", text, StringComparison.Ordinal);
        Assert.Contains("sa-abc123", text, StringComparison.Ordinal);
        Assert.Contains("Deep research on SERV", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SubagentTaskState.Queued)]
    [InlineData(SubagentTaskState.Running)]
    [InlineData(SubagentTaskState.Revising)]
    public void Build_NonTerminalState_DoesNotClaimSuccess(SubagentTaskState state)
    {
        // These should never reach the notifier, but if one ever does it must not be dressed
        // up as a completed task — that is precisely the class of bug being fixed.
        var text = SubagentCompletionNotice.Build(Task(state));

        Assert.DoesNotContain("[Background task completed]", text, StringComparison.Ordinal);
    }

    // ── Missing result ─────────────────────────────────────────────────

    [Fact]
    public void Build_NoResultRecorded_StillProducesAUsableEnvelope()
    {
        var text = SubagentCompletionNotice.Build(Task(SubagentTaskState.Completed, result: null));

        Assert.Contains("[no result recorded]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailedWithNoResultRecorded_StillReadsAsAFailure()
    {
        var text = SubagentCompletionNotice.Build(Task(SubagentTaskState.Failed, result: null));

        Assert.Contains("[Background task FAILED]", text, StringComparison.Ordinal);
        Assert.Contains("[no result recorded]", text, StringComparison.Ordinal);
    }
}
