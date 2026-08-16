using System.ComponentModel.DataAnnotations;

namespace Cortex.Contained.Contracts.Config;

/// <summary>Agent configuration (lives inside the container -- NO secrets).</summary>
public sealed class AgentConfig
{
    /// <summary>Agent display name.</summary>
    [Required]
    public string Name { get; set; } = "Cortex";

    /// <summary>System prompt for the agent.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Maximum tokens per completion.</summary>
    [Range(1, 1_000_000)]
    public int MaxTokens { get; set; } = 8192;

    /// <summary>LLM temperature (0.0 to 2.0).</summary>
    [Range(0.0, 2.0)]
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// Maximum silence, in seconds, before the FIRST token of a streamed response arrives.
    /// Time-to-first-token grows with prompt size, so this stays the more generous of the two
    /// from <see cref="LlmStreamIdleTimeoutSeconds"/>. 0 disables the guard.
    /// </summary>
    [Range(0, 3600)]
    public int LlmFirstTokenTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Maximum silence, in seconds, BETWEEN streamed chunks once generation has started. Needed
    /// because HttpClient.Timeout stops applying once response headers are read, leaving a
    /// silent provider stream unbounded. 0 disables the guard.
    /// </summary>
    [Range(0, 3600)]
    public int LlmStreamIdleTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// How many times a subagent round is re-issued when the provider stream faults with a
    /// transient fault AFTER content has already been streamed. <c>DirectLlmClient</c>'s retry
    /// and failover are pre-content only, so without this a single mid-stream stall ends a
    /// long-running subagent outright. 0 disables the behaviour.
    /// </summary>
    [Range(0, 10)]
    public int SubagentTransientStreamRetries { get; set; } = 2;

    /// <summary>
    /// Absolute wall-clock ceiling on a single streamed LLM response, in seconds. Backstop for
    /// a stream that stays technically alive but makes no progress: provider heartbeats re-arm
    /// the idle budget, so without this a chatty-but-stuck stream would never terminate.
    /// 0 uses the built-in default (30 minutes); a negative value disables the ceiling.
    /// <para>
    /// WORST CASE, deliberately: one round can spend up to
    /// <see cref="LlmStreamIdleTimeoutSeconds"/> x (1 + <see cref="SubagentTransientStreamRetries"/>)
    /// plus backoff in dead air before failing, bounded per attempt by this ceiling.
    /// </para>
    /// </summary>
    [Range(-1, 21600)]
    public int LlmStreamMaxDurationSeconds { get; set; }

    /// <summary>
    /// Requested reasoning effort for the agent's own turns
    /// (<c>minimal</c>|<c>low</c>|<c>medium</c>|<c>high</c>|<c>max</c>). Null or empty leaves each
    /// provider on its own default. Resolved per provider and per model before it reaches the
    /// wire, so an unsupported value or model simply sends nothing. Utility calls (memory
    /// extraction, compaction, image description) deliberately never carry it.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Security settings.</summary>
    public SecurityConfig Security { get; set; } = new();

    /// <summary>Session management settings.</summary>
    public SessionConfig Sessions { get; set; } = new();

    /// <summary>
    /// Available model IDs (for validation/display only).
    /// Actual provider config and API keys live in Bridge config.
    /// </summary>
    public List<ModelDefinition> AvailableModels { get; set; } = [];

    /// <summary>Enabled tool names (empty = all).</summary>
    public List<string> EnabledTools { get; set; } = [];

    /// <summary>
    /// Maximum tool-call rounds for sub-agents. The real termination signals are:
    /// the model stops calling tools, context window fills up, or doom loop detection fires.
    /// This cap is a safety-net circuit breaker for pathological cases.
    /// 0 = use default (200).
    /// </summary>
    [Range(0, 10_000)]
    public int MaxSubagentRounds { get; set; }

    /// <summary>
    /// Maximum number of subagent tasks that can run concurrently (1-50).
    /// Additional tasks are queued and start automatically when a slot opens.
    /// Out-of-range values are rejected, never clamped — see <see cref="SubagentConcurrencyLimits"/>.
    /// </summary>
    [Range(SubagentConcurrencyLimits.Minimum, SubagentConcurrencyLimits.Maximum)]
    public int MaxConcurrentSubagents { get; set; } = SubagentConcurrencyLimits.Default;

    /// <summary>Settings that control how images are aged out of the context window.</summary>
    public ImageAgingConfig ImageAging { get; set; } = new();
}

/// <summary>Security settings for the agent.</summary>
public sealed class SecurityConfig
{
    /// <summary>Shared authentication token.</summary>
    [Required]
    public string HubToken { get; set; } = string.Empty;

    /// <summary>Rate limiting configuration.</summary>
    public RateLimitConfig RateLimiting { get; set; } = new();
}

/// <summary>Rate limiting configuration.</summary>
public sealed class RateLimitConfig
{
    [Range(1, 10_000)]
    public int MaxAttempts { get; set; } = 10;

    [Range(1, 86_400)]
    public int WindowSeconds { get; set; } = 60;

    [Range(1, 86_400)]
    public int LockoutSeconds { get; set; } = 300;
}

/// <summary>Session management settings.</summary>
public sealed class SessionConfig
{
    [Range(1, 10_000)]
    public int MaxHistory { get; set; } = 100;

    [Range(1, 1_440)]
    public int IdleResetMinutes { get; set; } = 360;

    /// <summary>
    /// How much recent conversation a fired session timer's composer run is given, counted in
    /// turns (user and agent messages; tool traffic rides along with its turn). Enough to know
    /// what is going on, not so much that a small, frequent call re-reads the whole chat.
    /// </summary>
    [Range(1, 200)]
    public int TimerComposerTailTurns { get; set; } = 16;
}

/// <summary>Definition of an available LLM model.</summary>
public sealed class ModelDefinition
{
    [Required]
    public string Id { get; set; } = string.Empty;

    [Range(1, 10_000_000)]
    public int ContextWindow { get; set; } = 128_000;

    [Range(1, 1_000_000)]
    public int MaxOutputTokens { get; set; } = 8_192;
}
