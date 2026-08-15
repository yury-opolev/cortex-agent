namespace Cortex.Contained.Agent.Host.Llm.Providers;

/// <summary>
/// Everything needed to diagnose one inactivity-watchdog breach from the log alone: which
/// request stalled, in which phase, after how much output, on how large a context, and for how
/// long.
/// </summary>
internal sealed record LlmStreamStallReport
{
    /// <summary>Which budget was breached.</summary>
    public required LlmStreamStallPhase Phase { get; init; }

    /// <summary>The budget that was breached.</summary>
    public required TimeSpan Budget { get; init; }

    /// <summary>Actual silence measured before the guard gave up.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// True when content had already been delivered to the caller. The single most diagnostic
    /// field: it is exactly what puts the fault beyond the reach of the facade's pre-content
    /// retry and failover.
    /// </summary>
    public required bool ContentEmitted { get; init; }

    /// <summary>Content-bearing chunks received before the stall.</summary>
    public required int ChunksReceived { get; init; }

    /// <summary>Keep-alive heartbeats received before the stall.</summary>
    public required int KeepAlivesReceived { get; init; }

    /// <summary>Characters of content text received before the stall.</summary>
    public required int ContentCharsReceived { get; init; }

    /// <inheritdoc cref="LlmStreamContext.ConversationId"/>
    public string? ConversationId { get; init; }

    /// <inheritdoc cref="LlmStreamContext.RequestId"/>
    public string? RequestId { get; init; }

    /// <inheritdoc cref="LlmStreamContext.Model"/>
    public string? Model { get; init; }

    /// <inheritdoc cref="LlmStreamContext.Provider"/>
    public string? Provider { get; init; }

    /// <inheritdoc cref="LlmStreamContext.PromptChars"/>
    public int PromptChars { get; init; }
}
