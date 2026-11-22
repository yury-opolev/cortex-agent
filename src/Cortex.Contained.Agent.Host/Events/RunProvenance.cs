using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>
/// Identity of one run (a main-agent turn or a subagent execution): every event it produces
/// carries <see cref="RunId"/>, <see cref="AuthorKind"/> and <see cref="AuthorId"/>, which is how a
/// delivery is linked to the run, calls and results behind it.
/// </summary>
/// <param name="RunId">The run ID (<c>run_…</c>).</param>
/// <param name="AuthorKind">See <see cref="AuthorKinds"/>.</param>
/// <param name="AuthorId">Task or subagent ID, when there is one.</param>
/// <param name="ChannelId">
/// The conversation the run belongs to; its events share that channel's erasure domain. Null for
/// worker runs without a conversation (subagents), whose events stay metadata-only.
/// </param>
/// <param name="Label">Human-readable producer, e.g. <c>scheduled task 1bffa504 ("Morning brief")</c>.</param>
public sealed record RunProvenance(string RunId, string AuthorKind, string? AuthorId, string? ChannelId, string? Label)
{
    private const string ScheduledConversationPrefix = "scheduled-";

    /// <summary>The run that processes <paramref name="message"/>.</summary>
    public static RunProvenance ForMessage(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var runId = EventIds.NewRunId();
        return message.Source switch
        {
            AgentMessageSource.ScheduledTask => new RunProvenance(
                runId,
                AuthorKinds.ScheduledTask,
                message.ConversationId.StartsWith(ScheduledConversationPrefix, StringComparison.Ordinal)
                    ? message.ConversationId[ScheduledConversationPrefix.Length..]
                    : null,
                message.ChannelId,
                message.TriggerLabel),
            AgentMessageSource.SessionTimer => new RunProvenance(runId, AuthorKinds.Timer, null, message.ChannelId, message.TriggerLabel),
            AgentMessageSource.SubagentCompletion => new RunProvenance(
                runId,
                AuthorKinds.Main,
                null,
                message.ChannelId,
                message.SubagentTaskId is { } taskId ? $"completion of subagent {taskId}" : message.TriggerLabel),
            _ => new RunProvenance(runId, AuthorKinds.Main, null, message.ChannelId, message.TriggerLabel),
        };
    }

    /// <summary>The run of subagent <paramref name="taskId"/> (no conversation: metadata-only events).</summary>
    public static RunProvenance ForSubagent(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return new RunProvenance(EventIds.NewRunId(), AuthorKinds.Subagent, taskId, null, $"subagent {taskId}");
    }
}
