using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Per-<see cref="AgentMessageSource"/> processing policy. Centralizes the source-conditional
/// behavior that was previously scattered across <c>AgentRuntime.ProcessQueuedMessageAsync</c>
/// and the mid-turn pending-message drain.
/// </summary>
internal sealed record MessageSourceBehavior(
    bool RunInEphemeralSession,
    bool IsInternalToHistory,
    bool UseProactiveDelivery,
    bool HandlesSlashCommands,
    bool SetsConversationTitleFromText,
    bool RunsMemoryExtraction,
    string? PendingInjectionLabelPrefix,
    LlmMessageType PendingInjectionMessageType)
{
    /// <summary>Resolves the behavior policy for a message source.</summary>
    public static MessageSourceBehavior For(AgentMessageSource source) => source switch
    {
        AgentMessageSource.User => new(
            RunInEphemeralSession: false,
            IsInternalToHistory: false,
            UseProactiveDelivery: false,
            HandlesSlashCommands: true,
            SetsConversationTitleFromText: true,
            RunsMemoryExtraction: true,
            PendingInjectionLabelPrefix: null,
            PendingInjectionMessageType: LlmMessageType.Normal),
        AgentMessageSource.ScheduledTask => new(
            RunInEphemeralSession: true,
            IsInternalToHistory: true,
            UseProactiveDelivery: true,
            HandlesSlashCommands: false,
            SetsConversationTitleFromText: false,
            RunsMemoryExtraction: false,
            PendingInjectionLabelPrefix: "[Scheduled Task] ",
            PendingInjectionMessageType: LlmMessageType.ScheduledTaskInstruction),
        AgentMessageSource.SubagentCompletion => new(
            RunInEphemeralSession: false,
            IsInternalToHistory: true,
            UseProactiveDelivery: false,
            HandlesSlashCommands: false,
            SetsConversationTitleFromText: false,
            RunsMemoryExtraction: false,
            PendingInjectionLabelPrefix: "[Background Task Completed] ",
            PendingInjectionMessageType: LlmMessageType.ScheduledTaskInstruction),
        // CodingAgentInjection (and any future source): non-user injected message — NOT internal,
        // not ephemeral, normal delivery, no slash/title/extraction, no pending prefix, instruction type.
        _ => new(
            RunInEphemeralSession: false,
            IsInternalToHistory: false,
            UseProactiveDelivery: false,
            HandlesSlashCommands: false,
            SetsConversationTitleFromText: false,
            RunsMemoryExtraction: false,
            PendingInjectionLabelPrefix: null,
            PendingInjectionMessageType: LlmMessageType.ScheduledTaskInstruction),
    };
}
