using System.Text.Json.Serialization;

namespace Cortex.Contained.Channels.CloudMessaging.Envelope;

/// <summary>
/// Type-specific payload carried inside a <see cref="CloudEnvelope"/>.
/// All optional fields are null when not relevant for a given frame type.
/// </summary>
public sealed class CloudPayload
{
    // ── text / stream-chunk ──────────────────────────────────────────

    /// <summary>Message text (used by "text" and "stream-chunk" types).</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    // ── finalize ─────────────────────────────────────────────────────

    /// <summary>
    /// True when the finalize frame closes a "thinking" (pre-tool narration) lane.
    /// Maps to <see cref="Cortex.Contained.Contracts.Messages.OutboundMessage.IsThinking"/>.
    /// </summary>
    [JsonPropertyName("isThinking")]
    public bool? IsThinking { get; init; }

    /// <summary>Final text of the completed message (used by "finalize").</summary>
    [JsonPropertyName("finalText")]
    public string? FinalText { get; init; }

    // ── control ───────────────────────────────────────────────────────

    /// <summary>Control action: "abort", "status", or "presence".</summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    // ── error ─────────────────────────────────────────────────────────

    /// <summary>Machine-readable error code (used by "error").</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>Human-readable error message (used by "error").</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
