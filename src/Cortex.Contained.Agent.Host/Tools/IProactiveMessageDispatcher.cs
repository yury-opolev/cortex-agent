namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Shared abstraction for "push a proactive (agent-initiated) message to a channel
/// via the Bridge." Used by tools that deliver content the agent originated rather
/// than responding to a user message (e.g. <c>send_message</c>, <c>transfer_session</c>).
/// <para>
/// Behind the dispatcher, the Bridge handles delivery, including the
/// voice-ring/invite fallback when the target is a voice channel and the user
/// isn't currently present (see the Discord channel's <c>ProactiveVoiceCoordinator</c>).
/// Callers do not need to check channel readiness — the dispatcher always tries,
/// and the Bridge produces a sensible delivery regardless of presence.
/// </para>
/// </summary>
internal interface IProactiveMessageDispatcher
{
    /// <summary>
    /// Push <paramref name="text"/> to <paramref name="channelId"/> via the Bridge.
    /// On success, also records the dispatch to <paramref name="context"/> (so
    /// AgentRuntime can inject it into the target session's history after the tool
    /// loop completes) and persists to <see cref="Storage.MessageStore"/> with
    /// category <see cref="Contracts.Hub.MessageCategory.Proactive"/>.
    /// </summary>
    /// <param name="channelId">Canonical target channel id (e.g. <c>voice-default</c>).</param>
    /// <param name="text">Message body. Must be non-empty.</param>
    /// <param name="context">
    /// Tool execution context for deferred-injection bookkeeping. Pass <c>null</c>
    /// when dispatching from a path that has no tool loop (in which case the
    /// receiving session's history won't get an automatic agent-side echo).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProactiveDispatchResult> DispatchAsync(
        string channelId,
        string text,
        ToolExecutionContext? context,
        CancellationToken cancellationToken);
}

/// <summary>Outcome of an <see cref="IProactiveMessageDispatcher.DispatchAsync"/> call.</summary>
internal sealed record ProactiveDispatchResult
{
    /// <summary>Whether the dispatch was accepted by the Bridge.</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Error string when <see cref="Success"/> is false. Either a hard reason
    /// (<c>"Bridge is not connected"</c>) or the Bridge-supplied failure detail
    /// (<c>"Failed to send message: channel not found"</c>).
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Conversation id assigned by the Bridge on success, when one is available.
    /// Useful for correlating proactive messages with downstream events.
    /// </summary>
    public string? ConversationId { get; init; }
}
