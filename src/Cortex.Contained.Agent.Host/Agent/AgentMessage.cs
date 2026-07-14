using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// A unified work item for the agent's processing queue.
/// Both user messages (via SignalR) and scheduled tasks write to the same queue;
/// a single consumer loop processes them sequentially per conversation.
/// </summary>
public sealed record AgentMessage
{
    /// <summary>
    /// The Bridge-facing conversation ID (channel ID). Used for routing callbacks
    /// (chunks, completion, errors) back to the correct channel.
    /// Each channel maps directly to its own session — e.g. <c>webchat-default</c>,
    /// <c>discord-dm</c>, <c>discord-guild</c>, <c>voice-default</c>.
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// The channel ID that originated this message (e.g. <c>webchat-default</c>,
    /// <c>discord-dm</c>). Used for routing and session isolation.
    /// </summary>
    public required string ChannelId { get; init; }

    /// <summary>
    /// The text content. For user messages this is what the user typed;
    /// for scheduled tasks this is the instruction/prompt for the LLM.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>Where this message originated.</summary>
    public required AgentMessageSource Source { get; init; }

    /// <summary>
    /// Hashed sender identity (only meaningful for <see cref="AgentMessageSource.User"/> messages).
    /// </summary>
    public string? SenderIdHash { get; init; }

    /// <summary>Media attachments (images, etc.) from the user message.</summary>
    public IReadOnlyList<MediaAttachment>? Attachments { get; init; }

    /// <summary>End-to-end correlation ID for tracing.</summary>
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>When the message was created / enqueued.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether this message originated from a voice channel (real-time STT).
    /// When true, the agent adjusts its response style for spoken output.
    /// </summary>
    public bool IsVoice { get; init; }

    /// <summary>
    /// The subagent task whose durable completion notification this message carries.
    /// Set ONLY by <see cref="SubagentExecutionCoordinator"/> when it enqueues a
    /// <see cref="AgentMessageSource.SubagentCompletion"/> message. The runtime confirms
    /// delivery (<c>MarkNotificationDelivered</c>) after the parent turn's final response
    /// lands, or releases the claim (<c>ReleaseNotification</c>) on failure so the
    /// notification is redelivered — at-least-once, never silently dropped.
    /// </summary>
    public string? SubagentTaskId { get; init; }
}

/// <summary>
/// Identifies the origin of an <see cref="AgentMessage"/>.
/// </summary>
public enum AgentMessageSource
{
    /// <summary>Message sent by the user via a channel (WebChat, Discord, etc.).</summary>
    User = 0,

    /// <summary>
    /// Message injected by the scheduler when a task fires.
    /// Processed through the LLM like a user message but prefixed
    /// with context so the LLM knows it's a scheduled task.
    /// </summary>
    ScheduledTask = 1,

    /// <summary>
    /// Message injected when a background subagent completes.
    /// Processed on the parent conversation's lane through the full tool loop
    /// so the main agent can decide to respond, chain tasks, or do nothing.
    /// </summary>
    SubagentCompletion = 2,

    /// <summary>
    /// Synthetic message injected when an external-agent (Claude Code) session emits
    /// a terminal event (final result, permission ask, clarification, error).
    /// The agent runtime processes it through the normal tool loop so the LLM relays
    /// the content to the user according to the relay rules in the system prompt.
    /// </summary>
    CodingAgentInjection = 3,

}
