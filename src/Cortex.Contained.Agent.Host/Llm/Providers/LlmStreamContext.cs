namespace Cortex.Contained.Agent.Host.Llm.Providers;

/// <summary>
/// Identity of the request a <see cref="LlmStreamIdleGuard"/> is watching, so a breach can be
/// attributed to a conversation, a model and a context size instead of being an anonymous
/// timeout. Optional: the guard works without it, and tests routinely omit it.
/// </summary>
internal sealed record LlmStreamContext
{
    /// <summary>Conversation the stream belongs to. For a subagent this is "subagent-{taskId}".</summary>
    public string? ConversationId { get; init; }

    /// <summary>Per-request correlation id.</summary>
    public string? RequestId { get; init; }

    /// <summary>Model the request was issued against.</summary>
    public string? Model { get; init; }

    /// <summary>Provider credential name the request was routed to.</summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Total characters of prompt sent. A cheap, honest proxy for context size — the stall this
    /// telemetry exists for correlates with a very large accumulated context, and that
    /// correlation is invisible without a size on the breach record.
    /// </summary>
    public int PromptChars { get; init; }
}
