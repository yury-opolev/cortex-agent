namespace Cortex.Contained.Contracts.Llm;

/// <summary>
/// Abstraction for streaming LLM completions. Implemented by <c>DirectLlmClient</c>
/// in the Agent Host; mockable in tests.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Non-streaming completion. Returns the full response in one call.
    /// Used for background tasks like memory extraction where streaming is unnecessary.
    /// </summary>
    Task<LlmCompletionResult> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams completion chunks for the given request.
    /// </summary>
    IAsyncEnumerable<LlmStreamChunk> StreamCompleteAsync(
        LlmCompletionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Request from the agent to the Bridge for an LLM completion.
/// Contains no credentials -- the Bridge handles authentication.
/// </summary>
public sealed record LlmCompletionRequest
{
    /// <summary>Requested model ID (e.g., "gpt-4o", "claude-sonnet-4-20250514").</summary>
    public required string Model { get; init; }

    /// <summary>Conversation messages (system, user, assistant turns).</summary>
    public required IReadOnlyList<LlmMessage> Messages { get; init; }

    /// <summary>Optional tool definitions for function calling.</summary>
    public IReadOnlyList<LlmToolDefinition>? Tools { get; init; }

    /// <summary>Sampling temperature (0.0 - 2.0).</summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>Maximum tokens in the response.</summary>
    public int MaxTokens { get; init; } = 8192;

    /// <summary>Correlation ID for tracking across boundaries.</summary>
    public required string RequestId { get; init; }

    /// <summary>Conversation ID for cost tracking / rate limiting.</summary>
    public required string ConversationId { get; init; }
}

/// <summary>
/// Classifies messages so different parts of the system (seeding, compaction,
/// chat UI) can decide what to include or exclude.
/// </summary>
public enum LlmMessageType
{
    /// <summary>Normal user/assistant message — full lifecycle.</summary>
    Normal = 0,

    /// <summary>
    /// Compaction summary (the "What did we do so far?" pair).
    /// In LLM context: yes. Seed: no (it IS the seed replacement).
    /// </summary>
    CompactionSummary,

    /// <summary>
    /// Slash command response (/compact, /context).
    /// In LLM context: no. Seed: no. Chat UI: yes (via Bridge separately).
    /// </summary>
    SystemCommand,

    /// <summary>
    /// Scheduled task instruction injected into the scheduled-task session.
    /// In LLM context: yes (during execution). Seed: no.
    /// </summary>
    ScheduledTaskInstruction,

    /// <summary>
    /// Proactive message injected into the target channel session after successful delivery.
    /// In LLM context: yes. Seed: yes (also in SQLite).
    /// </summary>
    Proactive,
}

/// <summary>A message in an LLM conversation.</summary>
public sealed record LlmMessage
{
    /// <summary>Role: "system", "user", "assistant", "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Text content (convenience for text-only messages).</summary>
    public string? Content { get; init; }

    /// <summary>
    /// Multimodal content blocks (text + images). When set, takes precedence over
    /// <see cref="Content"/> for building the LLM request. Use this for messages
    /// that contain images alongside text.
    /// </summary>
    public IReadOnlyList<LlmContentBlock>? ContentBlocks { get; init; }

    /// <summary>Tool calls (for assistant messages with function calls).</summary>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }

    /// <summary>Tool call ID (for tool response messages).</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Classifies this message for filtering by seeding and chat UI.</summary>
    public LlmMessageType MessageType { get; init; }

    /// <summary>
    /// Computed for backward compatibility. True when the message should be hidden
    /// from the user's chat history (everything except Normal and Proactive).
    /// </summary>
    public bool IsInternal => MessageType is not LlmMessageType.Normal
                                          and not LlmMessageType.Proactive;

    /// <summary>
    /// When the message was created. Used for displaying timestamps in chat history.
    /// Not sent to the LLM.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A content block within an LLM message. Supports text and image types,
/// providing a unified model that serializes to provider-specific formats
/// (Anthropic image blocks, OpenAI image_url blocks).
/// </summary>
public sealed record LlmContentBlock
{
    /// <summary>Block type: "text" or "image".</summary>
    public required string Type { get; init; }

    /// <summary>Text content (when Type = "text").</summary>
    public string? Text { get; init; }

    /// <summary>Base64-encoded image data (when Type = "image").</summary>
    public string? ImageData { get; init; }

    /// <summary>MIME type of the image, e.g. "image/png" (when Type = "image").</summary>
    public string? ImageMediaType { get; init; }

    /// <summary>
    /// Cached textual description of the image (when Type = "image").
    /// Populated lazily by <c>ContextManager</c> the first time the image is stripped
    /// so subsequent aging passes don't re-run the describer. Settable (not init-only)
    /// because the cache is filled after the block is created.
    /// </summary>
    public string? ImageDescription { get; set; }

    /// <summary>Creates a text content block.</summary>
    public static LlmContentBlock TextBlock(string text) => new() { Type = "text", Text = text };

    /// <summary>Creates an image content block from base64 data.</summary>
    public static LlmContentBlock ImageBlock(string base64Data, string mediaType) =>
        new() { Type = "image", ImageData = base64Data, ImageMediaType = mediaType };
}

/// <summary>Definition of a tool available to the LLM.</summary>
public sealed record LlmToolDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>JSON Schema for parameters.</summary>
    public required string ParametersSchema { get; init; }
}

/// <summary>A tool call requested by the LLM.</summary>
public sealed record LlmToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>JSON-encoded arguments.</summary>
    public required string Arguments { get; init; }
}

/// <summary>
/// Full (non-streaming) LLM completion result from the Bridge.
/// </summary>
public sealed record LlmCompletionResult
{
    public required bool Success { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }
    public string? FinishReason { get; init; }
    public LlmTokenUsage? Usage { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Which provider actually served this request.</summary>
    public string? ProviderId { get; init; }
}

/// <summary>
/// A single streaming chunk from the Bridge during LLM completion.
/// </summary>
public sealed record LlmStreamChunk
{
    /// <summary>Text delta in this chunk.</summary>
    public string? ContentDelta { get; init; }

    /// <summary>
    /// Tool call deltas in this chunk (may contain multiple when the API sends
    /// several tool call fragments in one SSE event).
    /// </summary>
    public IReadOnlyList<LlmToolCallDelta>? ToolCallDeltas { get; init; }

    /// <summary>True if this is the final chunk.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Final token usage (only on last chunk).</summary>
    public LlmTokenUsage? Usage { get; init; }

    /// <summary>Finish reason (only on last chunk).</summary>
    public string? FinishReason { get; init; }

    /// <summary>Error if the stream failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>A partial tool call in a streaming chunk.</summary>
public sealed record LlmToolCallDelta
{
    /// <summary>
    /// Positional index identifying which tool call this delta belongs to.
    /// Stable across all chunks for the same tool call within one response.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Unique ID for this tool call. Only present on the first chunk for a
    /// given tool call; null on subsequent argument-continuation chunks.
    /// </summary>
    public string? Id { get; init; }

    public string? Name { get; init; }

    /// <summary>Partial JSON arguments appended in this chunk.</summary>
    public string? ArgumentsDelta { get; init; }
}

/// <summary>Token usage statistics for an LLM request.</summary>
public sealed record LlmTokenUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }

    /// <summary>Tokens written to the prompt cache (Anthropic: cache_creation_input_tokens).</summary>
    public int CacheWriteTokens { get; init; }

    /// <summary>Tokens read from the prompt cache (Anthropic: cache_read_input_tokens).</summary>
    public int CacheReadTokens { get; init; }
}
