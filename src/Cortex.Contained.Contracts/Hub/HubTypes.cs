using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Contracts.Hub;

/// <summary>A message sent from the Bridge to the Agent Hub.</summary>
public sealed record HubInboundMessage
{
    public required string ConversationId { get; init; }

    /// <summary>
    /// Channel identifier (e.g. "webchat-default", "discord-dm", "discord-guild", "voice-default").
    /// Used by the Agent for per-channel session management and propagated to tool execution context.
    /// </summary>
    public required string ChannelId { get; init; }

    /// <summary>Hashed sender ID (original stays in Bridge for privacy).</summary>
    public required string SenderIdHash { get; init; }

    public required string Text { get; init; }
    public IReadOnlyList<MediaAttachment>? Attachments { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Correlation ID for end-to-end request tracing. Generated at ingress if not provided.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Whether this message originated from a voice channel (real-time STT).
    /// When true, the agent adjusts its response style for spoken output
    /// (conversational tone, no markdown, progressive disclosure).
    /// </summary>
    public bool IsVoice { get; init; }
}

/// <summary>Result of sending a message to the agent.</summary>
public sealed record SendMessageResult
{
    public required bool Accepted { get; init; }
    public string? ConversationId { get; init; }
    public string? RejectionReason { get; init; }
}

/// <summary>A streaming response chunk from the agent.</summary>
public sealed record ResponseChunkMessage
{
    public required string ConversationId { get; init; }
    public required string Text { get; init; }
    public required int SequenceNumber { get; init; }
    public bool IsComplete { get; init; }

    /// <summary>Correlation ID for end-to-end request tracing.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>A complete response from the agent.</summary>
public sealed record ResponseCompleteMessage
{
    public required string ConversationId { get; init; }
    public required string MessageId { get; init; }
    public required string FullText { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public TokenUsage? Usage { get; init; }

    /// <summary>Correlation ID for end-to-end request tracing.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Controls how the Bridge persists and filters this message.
    /// Defaults to <see cref="MessageCategory.Normal"/>.
    /// </summary>
    public MessageCategory Category { get; init; }

    /// <summary>
    /// When true, this segment is pre-tool narration (e.g. "Let me check the log first.")
    /// rather than the final answer. The web chat renders thinking segments in a dimmed,
    /// collapsible lane so they are never overwritten by later segments. Defaults to false;
    /// finalize-only channels (Discord/voice) ignore it.
    /// </summary>
    public bool IsThinking { get; init; }
}

/// <summary>
/// Controls how persisted messages are filtered for chat history and seeding.
/// Stored as an integer in SQLite.
/// </summary>
public enum MessageCategory
{
    /// <summary>Normal message — visible everywhere (chat UI, seeding).</summary>
    Normal = 0,

    /// <summary>Internal message — hidden from chat UI and seeding.
    /// Used for scheduled task instructions/responses.</summary>
    Internal = 1,

    /// <summary>System/error message — visible in chat UI (dimmed) but excluded from
    /// seeding. Used for LLM errors, slash command responses (/compact, /context).</summary>
    System = 2,

    /// <summary>Proactive message — agent-initiated content sent via <c>send_message</c> tool.
    /// Visible in chat UI (with proactive badge) but excluded from seeding to avoid
    /// polluting the agent's context with its own outbound messages.</summary>
    Proactive = 3,

    /// <summary>Transfer breadcrumb — a user-facing marker indicating that a conversation
    /// was moved between channels via the <c>transfer_session</c> tool. Visible in chat UI
    /// (no special badge) but excluded from seeding so the marker never feeds back into
    /// the LLM context on a re-seed.</summary>
    Transfer = 4,
}

/// <summary>Information about tool execution.</summary>
public sealed record ToolExecutionMessage
{
    public required string ConversationId { get; init; }
    public required string ToolName { get; init; }
    public required ToolExecutionStatus Status { get; init; }
    public string? Input { get; init; }
    public string? Output { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>Correlation ID for end-to-end request tracing.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>Status of a tool execution.</summary>
public enum ToolExecutionStatus
{
    Started,
    Completed,
    Failed
}

/// <summary>An error from the agent.</summary>
public sealed record AgentErrorMessage
{
    public required string ConversationId { get; init; }
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }
    public bool IsRetryable { get; init; }

    /// <summary>Correlation ID for end-to-end request tracing.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>Agent status information.</summary>
public sealed record AgentStatusInfo
{
    public required AgentStatus Status { get; init; }
    public int ActiveConversations { get; init; }
    public string? CurrentModel { get; init; }
    public DateTimeOffset Uptime { get; init; }
}

/// <summary>Overall agent status.</summary>
public enum AgentStatus
{
    Idle,
    Processing,
    Streaming,
    Error,
    ShuttingDown
}

/// <summary>Information about a conversation.</summary>
public sealed record ConversationInfo
{
    public required string ConversationId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastMessageAt { get; init; }
    public int MessageCount { get; init; }
    public string? Title { get; init; }
}

/// <summary>Request to create a new conversation.</summary>
public sealed record CreateConversationRequest
{
    public string? Title { get; init; }
}

/// <summary>A chat message stored in history.</summary>
public sealed record HubChatMessage
{
    public required string MessageId { get; init; }

    /// <summary>"user" or "assistant".</summary>
    public required string Role { get; init; }

    public required string Text { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public IReadOnlyList<ToolExecutionMessage>? ToolCalls { get; init; }
    public TokenUsage? Usage { get; init; }
}

/// <summary>Token usage statistics.</summary>
public sealed record TokenUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
}

/// <summary>Health check response.</summary>
public sealed record HealthInfo
{
    public required bool Healthy { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Version { get; init; }

    /// <summary>
    /// Optional operational metrics snapshot from the agent (queue depths/peaks,
    /// messages processed, token-refresh health). Null when the agent did not
    /// populate metrics (e.g. an older agent build or the Bridge-side probe).
    /// Additive: consumers that do not understand it simply ignore it.
    /// </summary>
    public AgentMetricsSnapshot? Metrics { get; init; }
}

/// <summary>
/// Result of an OAuth token refresh, returned directly from the Bridge
/// to the agent via SignalR Client Results. This avoids the deadlock
/// that occurs when the Bridge tries to push credentials back as a
/// separate hub method call while another hub method is in progress.
/// </summary>
public sealed record TokenRefreshResult
{
    /// <summary>Whether the refresh succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Fresh OAuth access token (null on failure).</summary>
    public string? AccessToken { get; init; }

    /// <summary>Fresh OAuth refresh token (null on failure or if unchanged).</summary>
    public string? RefreshToken { get; init; }

    /// <summary>Unix ms when <see cref="AccessToken"/> expires. 0 = unknown.</summary>
    public long ExpiresAtMs { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}

/// <summary>Result from an on-demand conversation compaction.</summary>
public sealed record CompactConversationResult
{
    public bool Success { get; init; }
    public int MessagesBefore { get; init; }
    public int MessagesAfter { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result from an on-demand memory compaction sweep.</summary>
public sealed record CompactMemoriesResult
{
    public bool Success { get; init; }
    public int MemoriesChecked { get; init; }
    public int MemoriesMerged { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Result of probing an embedding endpoint for reachability and a correct
/// embedding dimension. Returned by <c>IMemoryHub.TestEmbeddingEndpoint</c> so the
/// probe runs from the agent's network context (where Docker-internal names resolve),
/// not the Bridge host. The Bridge surfaces this verbatim to the web UI as
/// <c>{ ok, dim, error }</c>.
/// </summary>
public sealed record EmbeddingProbeResult
{
    /// <summary>True when the endpoint responded 200 with the expected embedding dimension.</summary>
    public bool Ok { get; init; }

    /// <summary>The embedding dimension observed in the response, when one could be parsed.</summary>
    public int? Dim { get; init; }

    /// <summary>Human-readable error when <see cref="Ok"/> is false; null on success.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// A proactive message sent from the Agent to the user without
/// a preceding user message. Used by agent tools (e.g. send_message) and
/// the scheduler. The Bridge routes it to whichever channel the user was
/// last active on.
/// </summary>
public sealed record ProactiveMessage
{
    /// <summary>The message text to deliver.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Target channel ID (e.g. "webchat-default", "discord-dm").
    /// If null, the Bridge delivers to the user's last-active channel.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Target conversation ID. If null, the Bridge delivers to the
    /// most recent conversation on the target channel.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Optional media attachments (e.g. images the agent is sending). The Bridge
    /// copies these onto the outbound message so the channel uploads them.
    /// Additive — older consumers ignore it.
    /// </summary>
    public IReadOnlyList<MediaAttachment>? Attachments { get; init; }

    /// <summary>Correlation ID for end-to-end request tracing.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>Result of delivering a proactive message.</summary>
public sealed record ProactiveMessageResult
{
    /// <summary>Whether the message was delivered successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>The conversation ID the message was delivered to.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Error message if <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Sent by the Agent when a scheduled task finishes execution.
/// The Bridge persists the instruction and response to the
/// <c>scheduled-tasks</c> channel so they appear in history.
/// </summary>
public sealed record ScheduledTaskCompleteMessage
{
    /// <summary>Unique task ID (e.g. "scheduled-a1b2c3d4").</summary>
    public required string TaskId { get; init; }

    /// <summary>The instruction text that was sent to the LLM.</summary>
    public required string InstructionText { get; init; }

    /// <summary>The agent's final response text (may be empty if the agent only used tools).</summary>
    public required string ResponseText { get; init; }

    /// <summary>When the task started executing.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Correlation ID for end-to-end request tracing.</summary>
    public string? CorrelationId { get; init; }
}

// --- History Query DTOs ---

/// <summary>Paginated list of conversation summaries.</summary>
public sealed record ConversationListResult
{
    public required IReadOnlyList<ConversationSummaryDto> Conversations { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>Summary of a conversation for listing purposes.</summary>
public sealed record ConversationSummaryDto
{
    public required string ConversationId { get; init; }
    public required string ChannelId { get; init; }
    public required string Title { get; init; }
    public required int MessageCount { get; init; }
    public required DateTimeOffset LastMessageAt { get; init; }
}

/// <summary>
/// Per-channel summary for the history management UI: how many messages the
/// channel has and when the most recent one was written.
/// </summary>
public sealed record ChannelSummaryDto
{
    /// <summary>Channel identifier (e.g. "webchat-default", "discord-voice-default").</summary>
    public required string Id { get; init; }

    /// <summary>Total number of messages persisted for this channel.</summary>
    public required int MessageCount { get; init; }

    /// <summary>Timestamp of the most recent message in this channel.</summary>
    public required DateTimeOffset LastActivity { get; init; }
}

/// <summary>Paginated list of messages.</summary>
public sealed record MessageListResult
{
    public required IReadOnlyList<MessageEntryDto> Messages { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>A message entry returned from history queries.</summary>
public sealed record MessageEntryDto
{
    public required string MessageId { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? ChannelId { get; init; }
    public MessageCategory Category { get; init; }
}

// --- Memory Management DTOs ---

/// <summary>A memory item returned from the agent's memory store.</summary>
public sealed record MemoryItem
{
    public required string MemoryId { get; init; }
    public string? Title { get; init; }
    public required string Content { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A memory search result with similarity score.</summary>
public sealed record MemorySearchItem
{
    public required string MemoryId { get; init; }
    public string? Title { get; init; }
    public required string Content { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public float Score { get; init; }
}

/// <summary>Paginated list of memories.</summary>
public sealed record MemoryListResult
{
    public required IReadOnlyList<MemoryItem> Items { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>Request to create a new memory.</summary>
public sealed record MemoryCreateRequest
{
    public required string Content { get; init; }
    public string? Title { get; init; }
    public List<string>? Tags { get; init; }
}

/// <summary>Request to update an existing memory.</summary>
public sealed record MemoryUpdateRequest
{
    public required string MemoryId { get; init; }
    public string? Content { get; init; }
    public string? Title { get; init; }
    public List<string>? Tags { get; init; }
}

/// <summary>Request to search memories by semantic similarity.</summary>
public sealed record MemorySearchRequest
{
    public required string Query { get; init; }
    public int Limit { get; init; } = 10;
    public float? MinScore { get; init; }
    public List<string>? Tags { get; init; }
}

/// <summary>Runtime configuration update.</summary>
public sealed record AgentConfigUpdate
{
    public string? SystemPrompt { get; init; }
    public int? MaxTokens { get; init; }
    public double? Temperature { get; init; }

    /// <summary>
    /// Maximum tool-call rounds for sub-agents. 0 = use default (200).
    /// The real termination signals are context window and doom loop detection;
    /// this is a safety-net circuit breaker.
    /// </summary>
    public int? MaxSubagentRounds { get; init; }

    /// <summary>
    /// Maximum number of subagents that may run concurrently (1–50). Applied live
    /// to the SubagentRunnerRegistry without a container restart. The Bridge value is
    /// authoritative (pushed on connect/reconnect/watchdog reconstruction) — do not rely
    /// on the Agent's own YAML-mounted config, which can be stale/mismatched.
    /// </summary>
    public int? MaxConcurrentSubagents { get; init; }
}

/// <summary>Pushed speaker-identification (voice-id) settings.</summary>
public sealed record SpeakerIdConfig
{
    /// <summary>Whether voice-id is enabled. Default true.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>Memory configuration readable/writable at runtime.</summary>
public sealed record MemoryConfig
{
    /// <summary>Whether the built-in memory subsystem is enabled. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Cosine-similarity threshold (0.0–1.0) for the duplicate guard in IngestAsync.
    /// Set to 0 to disable duplicate detection. Default: 0.90.
    /// </summary>
    public float DuplicateThreshold { get; init; } = 0.90f;

    /// <summary>
    /// Cosine-similarity threshold (0.0–1.0) used by the compaction sweep
    /// to identify near-duplicate memories for merging. Default: 0.70.
    /// </summary>
    public float CompactionSimilarityThreshold { get; init; } = 0.70f;

    /// <summary>Whether the periodic compaction sweep is enabled.</summary>
    public bool CompactionEnabled { get; init; } = true;

    /// <summary>
    /// When true, idle sessions are compacted (LLM summarization) instead of wiped.
    /// When false, idle sessions are cleared completely (original behavior). Default: true.
    /// </summary>
    public bool IdleCompactionEnabled { get; init; } = true;

    /// <summary>
    /// Minutes of inactivity before a session is considered idle and either compacted
    /// or cleared (depending on <see cref="IdleCompactionEnabled"/>). Default: 360 (6h).
    /// Set to 0 to disable idle reset entirely.
    /// </summary>
    public int IdleResetMinutes { get; init; } = 360;

    /// <summary>
    /// Override for <see cref="ImageAgingConfig.PreserveRecentTurns"/>.
    /// Null = use static config. 0 = disable image aging entirely.
    /// </summary>
    public int? ImagePreserveRecentTurns { get; init; }

    /// <summary>
    /// Override for <see cref="ImageAgingConfig.DescribeOnStrip"/>.
    /// Null = use static config.
    /// </summary>
    public bool? ImageDescribeOnStrip { get; init; }

    /// <summary>
    /// Override for <see cref="ConversationCompactionConfig.PreserveRecentTurns"/>.
    /// Null = use static config. 0 = disable user-turn-based preservation.
    /// </summary>
    public int? CompactionPreserveRecentTurns { get; init; }

    /// <summary>Override for the Ollama embedding endpoint. Null/empty = the agent uses its configured/default endpoint.</summary>
    public string? OllamaEndpoint { get; init; }

    /// <summary>API key for the embedding endpoint (sent as Bearer). Null = no auth. Held in-memory only on the agent.</summary>
    public string? OllamaApiKey { get; init; }
}

/// <summary>
/// Discriminates how the agent should authenticate with a provider.
/// </summary>
public enum CredentialKind
{
    /// <summary>
    /// Static API key. Sent as <c>x-api-key</c> (Anthropic) or
    /// <c>Authorization: Bearer</c> (OpenAI-compatible). Never expires.
    /// </summary>
    ApiKey,

    /// <summary>
    /// Anthropic OAuth access token (from PKCE or device code flow).
    /// Sent as <c>Authorization: Bearer</c> to <c>api.claude.ai</c>.
    /// May expire; the agent requests a refresh from the Bridge.
    /// </summary>
    AnthropicOAuth,

    /// <summary>
    /// Anthropic setup token (from <c>claude setup-token</c>).
    /// Long-lived OAuth token (~1 year). Sent as <c>Authorization: Bearer</c>
    /// to <c>api.claude.ai</c>. No refresh needed.
    /// </summary>
    AnthropicSetupToken,

    /// <summary>
    /// GitHub OAuth token. Used directly as <c>Authorization: Bearer</c> with
    /// Copilot-specific request headers. No agent-side refresh path.
    /// </summary>
    GitHubOAuth,

    /// <summary>
    /// GitHub Personal Access Token. Must be exchanged for a short-lived Copilot
    /// API token before use (handled entirely within the agent).
    /// </summary>
    /// <remarks>
    /// Retained for backward compatibility with older agents. New pushes use
    /// <see cref="GitHubCopilotBearer"/> so the durable PAT never leaves the Bridge.
    /// </remarks>
    GitHubPat,

    /// <summary>
    /// Bridge-minted short-lived GitHub Copilot bearer token. The Bridge exchanges the
    /// durable PAT for this bearer and carries it in
    /// <see cref="LlmProviderCredential.AccessToken"/> plus
    /// <see cref="LlmProviderCredential.AccessTokenExpiresAt"/>; the agent uses it directly
    /// as <c>Authorization: Bearer</c> against <c>api.githubcopilot.com</c>. On a 401 the
    /// agent requests a fresh bearer from the Bridge via the same SignalR round-trip used for
    /// <see cref="AnthropicOAuth"/> — it never performs the PAT exchange itself, and the PAT
    /// never enters the container.
    /// </summary>
    GitHubCopilotBearer,
}

/// <summary>
/// LLM provider credentials pushed from the Bridge to the Agent.
/// The agent stores these in memory only (never persisted to disk)
/// and uses them to call LLM providers directly.
/// </summary>
public sealed record LlmCredentials
{
    /// <summary>Provider configurations with credentials.</summary>
    public required IReadOnlyList<LlmProviderCredential> Providers { get; init; }
}

/// <summary>Credentials and configuration for a single LLM provider.</summary>
public sealed record LlmProviderCredential
{
    /// <summary>Unique provider name (e.g., "anthropic", "openai", "github-copilot").</summary>
    public required string Name { get; init; }

    /// <summary>
    /// API type: "openai-completions", "anthropic-messages", "github-copilot-api".
    /// Determines which HTTP client logic the agent uses.
    /// </summary>
    public required string Api { get; init; }

    /// <summary>Base URL for the API (e.g., "https://api.githubcopilot.com").</summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Identifies the credential type and determines which auth fields are populated.
    /// </summary>
    public CredentialKind Kind { get; init; } = CredentialKind.ApiKey;

    /// <summary>
    /// Static API key or GitHub PAT. Non-null when <see cref="Kind"/> is
    /// <see cref="CredentialKind.ApiKey"/> or <see cref="CredentialKind.GitHubPat"/>.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// OAuth access token (or Bridge-minted Copilot bearer). Non-null when <see cref="Kind"/>
    /// is <see cref="CredentialKind.AnthropicOAuth"/>, <see cref="CredentialKind.GitHubOAuth"/>,
    /// or <see cref="CredentialKind.GitHubCopilotBearer"/>.
    /// Held in memory only — never persisted in the container.
    /// </summary>
    public string? AccessToken { get; init; }

    /// <summary>
    /// OAuth refresh token (Anthropic OAuth only).
    /// Non-null when <see cref="Kind"/> is <see cref="CredentialKind.AnthropicOAuth"/>.
    /// Used by the Bridge to obtain a new access token; passed to the agent so it can
    /// signal the Bridge when a refresh is needed.
    /// </summary>
    public string? RefreshToken { get; init; }

    /// <summary>
    /// Unix milliseconds at which <see cref="AccessToken"/> expires.
    /// 0 means no expiry information is available.
    /// Non-zero when <see cref="Kind"/> is <see cref="CredentialKind.AnthropicOAuth"/> or
    /// <see cref="CredentialKind.GitHubCopilotBearer"/> (the Bridge-minted bearer's expiry, so
    /// the agent's proactive-refresh guard re-mints ahead of rotation).
    /// </summary>
    public long AccessTokenExpiresAt { get; init; }

    /// <summary>Model IDs this provider serves.</summary>
    public required IReadOnlyList<string> Models { get; init; }

    /// <summary>
    /// Default model for this provider. Used when the provider is selected
    /// but no specific model is requested. Null = first model in <see cref="Models"/>.
    /// </summary>
    public string? DefaultModel { get; init; }

    /// <summary>
    /// Model to use for memory-related tasks (context steering, extraction, compaction).
    /// Null = falls back to <see cref="DefaultModel"/>.
    /// </summary>
    public string? MemoryModel { get; init; }

    /// <summary>
    /// Optional per-model metadata (context window, max output tokens).
    /// Models not listed here use default values (128k context, 8192 output).
    /// </summary>
    public IReadOnlyList<LlmModelMetadata>? ModelMetadata { get; init; }
}

/// <summary>Per-model metadata sent from Bridge to Agent Host alongside credentials.</summary>
public sealed record LlmModelMetadata
{
    /// <summary>Model ID (matches an entry in <see cref="LlmProviderCredential.Models"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Total context window size in tokens.</summary>
    public int ContextWindow { get; init; } = 128_000;

    /// <summary>Maximum output tokens per completion.</summary>
    public int MaxOutputTokens { get; init; } = 8_192;
}
