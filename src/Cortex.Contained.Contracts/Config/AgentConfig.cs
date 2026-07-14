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
