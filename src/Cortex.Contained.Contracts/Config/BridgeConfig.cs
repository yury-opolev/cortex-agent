using System.ComponentModel.DataAnnotations;

namespace Cortex.Contained.Contracts.Config;

/// <summary>Bridge configuration (lives on the Windows host -- holds ALL secrets).</summary>
public sealed class BridgeConfig
{
    /// <summary>Agent Hub SignalR URL.</summary>
    [Required]
    public string AgentHubUrl { get; set; } = "http://127.0.0.1:5100/hub/agent";

    /// <summary>Shared authentication token for Bridge-Hub communication.</summary>
    [Required, MinLength(8)]
    public string HubToken { get; set; } = string.Empty;

    /// <summary>Web UI settings.</summary>
    public WebUiConfig WebUi { get; set; } = new();

    /// <summary>Configured LLM providers.</summary>
    public List<LlmProviderConfig> LlmProviders { get; set; } = [];

    /// <summary>Global LLM proxy settings.</summary>
    public LlmProxyConfig LlmProxy { get; set; } = new();

    /// <summary>Per-channel configurations keyed by channel name.</summary>
    public Dictionary<string, ChannelConfig> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shared speech (STT/TTS) configuration used by Voice and Discord channels.</summary>
    public SpeechConfig Speech { get; set; } = new();

    /// <summary>Memory service settings (thresholds, compaction).</summary>
    public MemorySettingsConfig Memory { get; set; } = new();

    /// <summary>
    /// Maximum tool-call rounds for sub-agents (0 = default 200).
    /// The real termination signals are context window and doom loop detection;
    /// this is a safety-net circuit breaker.
    /// </summary>
    public int MaxSubagentRounds { get; set; }

    /// <summary>
    /// Maximum number of subagents that may run concurrently (1-50). Default 5.
    /// Out-of-range values are rejected, never clamped — see <see cref="SubagentConcurrencyLimits"/>.
    /// This is the Bridge-authoritative value pushed to the Agent on connect/reconnect/watchdog
    /// reconstruction (see <c>CredentialsPusher.PushAgentConfigAsync</c>).
    /// </summary>
    [Range(SubagentConcurrencyLimits.Minimum, SubagentConcurrencyLimits.Maximum)]
    public int MaxConcurrentSubagents { get; set; } = SubagentConcurrencyLimits.Default;

    /// <summary>Multi-tenant configuration. Maps tenant ID → tenant config.</summary>
    public Dictionary<string, TenantConfig> Tenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MCP plugin-system settings (master switch + configured servers).</summary>
    public McpSettingsConfig Mcp { get; set; } = new();
}

/// <summary>Memory service settings persisted on the Bridge host.</summary>
public sealed class MemorySettingsConfig
{
    /// <summary>Master built-in-memory switch. When false, memory tools are hidden,
    /// fact-extraction + compaction are skipped, and the embeddings sidecar is stopped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cosine-similarity threshold (0.0–1.0) for the duplicate guard in IngestAsync.
    /// Set to 0 to disable duplicate detection. Default: 0.90.
    /// </summary>
    public float DuplicateThreshold { get; set; } = 0.90f;

    /// <summary>
    /// Cosine-similarity threshold (0.0–1.0) used by the compaction sweep
    /// to identify near-duplicate memories for merging. Default: 0.70.
    /// </summary>
    public float CompactionSimilarityThreshold { get; set; } = 0.70f;

    /// <summary>Whether the periodic compaction sweep is enabled.</summary>
    public bool CompactionEnabled { get; set; } = true;

    /// <summary>
    /// When true, idle sessions are compacted (LLM summarization) instead of wiped.
    /// When false, idle sessions are cleared completely (original behavior). Default: true.
    /// </summary>
    public bool IdleCompactionEnabled { get; set; } = true;

    /// <summary>
    /// Minutes of inactivity before a session is considered idle and either compacted
    /// or cleared (depending on <see cref="IdleCompactionEnabled"/>). Default: 360 (6h).
    /// Set to 0 to disable idle reset entirely.
    /// </summary>
    public int IdleResetMinutes { get; set; } = 360;

    /// <summary>
    /// Recent user turns to preserve verbatim at the tail of the conversation
    /// when the summarization compaction runs. The recent tail is kept intact
    /// only when its combined size fits in 25% of the model's context window;
    /// otherwise the older tool-round preservation rule is used as a fallback.
    /// Set to 0 to disable. Default: 4.
    /// </summary>
    public int CompactionPreserveRecentTurns { get; set; } = 4;

    /// <summary>User-set remote embedding endpoint override (non-secret → YAML). Null/blank = local default.</summary>
    public string? EmbeddingEndpoint { get; set; }
}

/// <summary>LLM provider configuration (API keys, endpoints, models).</summary>
public sealed class LlmProviderConfig
{
    /// <summary>Provider name (e.g., "openai", "anthropic").</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Provider API type: "openai-completions", "anthropic-messages", "github-copilot-api".</summary>
    [Required]
    public string Api { get; set; } = string.Empty;

    /// <summary>Custom base URL for the API (optional).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>API key or OAuth token (encrypted at rest via DPAPI).</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// How to interpret <see cref="ApiKey"/>:
    /// <c>"bearer"</c> (default), <c>"oauth"</c> (GitHub OAuth token), or <c>"pat"</c> (GitHub PAT → token exchange).
    /// </summary>
    public string TokenType { get; set; } = "bearer";

    /// <summary>
    /// GitHub OAuth App client ID for the device flow (only used when <see cref="TokenType"/> is <c>"oauth"</c>
    /// for a GitHub Copilot provider).
    /// If null or empty, a built-in default is used. Users can register their own OAuth App at
    /// <c>https://github.com/settings/developers</c> and set this to their Client ID.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// GitHub auth host for the OAuth device/authorization flow (only for a GitHub Copilot
    /// provider). Null or empty means public <c>https://github.com</c>. Set to a GitHub
    /// Enterprise host (e.g. <c>https://your-org.ghe.com</c>) to authenticate against an
    /// enterprise instance. Used at setup/auth time; the runtime inference host is
    /// <see cref="BaseUrl"/>.
    /// </summary>
    public string? GithubBaseUrl { get; set; }

    /// <summary>
    /// OAuth refresh token (only used when <see cref="TokenType"/> is <c>"oauth"</c> for
    /// Anthropic Claude Pro/Max accounts). Loaded from encrypted storage at runtime; never
    /// stored in the YAML config file.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// OAuth access-token expiry as a Unix timestamp in milliseconds (0 = no expiry or not OAuth).
    /// When non-zero and the current time exceeds this value, <see cref="AnthropicProvider"/>
    /// will use <see cref="RefreshToken"/> to obtain a fresh access token before the next request.
    /// </summary>
    public long TokenExpiresAt { get; set; }

    /// <summary>
    /// Name of another provider whose API key should be reused (e.g., "github-copilot-api").
    /// Resolved during post-configuration if <see cref="ApiKey"/> is not set directly.
    /// </summary>
    public string? ApiKeyFrom { get; set; }

    /// <summary>Supported model IDs.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>
    /// Default model for this provider. Used when the provider is selected
    /// (e.g., via fallback) but no specific model is requested.
    /// Null = first model in the <see cref="Models"/> list.
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// Model to use for memory-related tasks (extraction, compaction).
    /// Can be a cheaper/smaller model to reduce costs.
    /// Null = falls back to <see cref="DefaultModel"/>.
    /// </summary>
    public string? MemoryModel { get; set; }

    /// <summary>
    /// Optional per-model metadata (context window, max output tokens).
    /// Only models that need non-default values need an entry here.
    /// Models listed in <see cref="Models"/> but not here use 128k context / 8192 output defaults.
    /// </summary>
    public List<LlmModelDefinition> ModelDefinitions { get; set; } = [];

    /// <summary>Rate limits for this provider.</summary>
    public LlmRateLimitConfig? RateLimits { get; set; }
}

/// <summary>Rate limits for an LLM provider.</summary>
public sealed class LlmRateLimitConfig
{
    public int? RequestsPerMinute { get; set; }
    public int? TokensPerMinute { get; set; }
}

/// <summary>
/// Per-model metadata for an LLM provider. Allows specifying context window
/// and max output tokens for individual models in the Bridge YAML config.
/// </summary>
public sealed class LlmModelDefinition
{
    /// <summary>Model ID (must match an entry in <see cref="LlmProviderConfig.Models"/>).</summary>
    [Required]
    public string Id { get; set; } = string.Empty;

    /// <summary>Total context window size in tokens.</summary>
    [Range(1, 10_000_000)]
    public int ContextWindow { get; set; } = 128_000;

    /// <summary>Maximum output tokens per completion.</summary>
    [Range(1, 1_000_000)]
    public int MaxOutputTokens { get; set; } = 8_192;
}

/// <summary>Global LLM proxy settings.</summary>
public sealed class LlmProxyConfig
{
    /// <summary>Provider fallback order when primary fails.</summary>
    public List<string> FallbackOrder { get; set; } = [];

    public CostTrackingConfig? CostTracking { get; set; }
}

/// <summary>Cost tracking settings.</summary>
public sealed class CostTrackingConfig
{
    public bool Enabled { get; set; }
    public decimal? MonthlyBudgetUsd { get; set; }
}

/// <summary>Web UI settings.</summary>
public sealed class WebUiConfig
{
    public bool Enabled { get; set; } = true;

    [Range(1, 65535)]
    public int Port { get; set; } = 5080;

    public string BindAddress { get; set; } = "127.0.0.1";
}

/// <summary>Per-channel configuration.</summary>
public sealed class ChannelConfig
{
    public bool Enabled { get; set; }

    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shared speech configuration for STT and TTS engines.
/// Used by Voice channel, Discord voice messages, and any future voice-enabled channels.
/// </summary>
public sealed class SpeechConfig
{
    /// <summary>Master voice switch. When false, STT, TTS, and voice-id are all off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Speech-to-text engine settings.</summary>
    public SttConfig Stt { get; set; } = new();

    /// <summary>Text-to-speech engine settings.</summary>
    public TtsConfig Tts { get; set; } = new();

    /// <summary>Speaker-identification (voice-id) settings.</summary>
    public VoiceIdConfig VoiceId { get; set; } = new();
}

/// <summary>Speaker-identification (voice-id) settings.</summary>
public sealed class VoiceIdConfig
{
    /// <summary>Whether speaker verification/enrollment is enabled (gated by <see cref="SpeechConfig.Enabled"/>).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Speech-to-text engine configuration.</summary>
public sealed class SttConfig
{
    /// <summary>Whether speech-to-text is enabled (gated by <see cref="SpeechConfig.Enabled"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>STT engine name. Currently only "whisper" is supported.</summary>
    public string Engine { get; set; } = "whisper";

    /// <summary>Path to the Whisper GGML model file (e.g., "ggml-base.bin").</summary>
    public string? WhisperModelPath { get; set; }

    /// <summary>Whisper language code (e.g., "en"). Use "auto" for auto-detection.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Optional initial prompt to bias domain vocabulary / proper
    /// nouns (e.g. "Cortex"). Empty = none. Applied on the final pass.</summary>
    public string? InitialPrompt { get; set; }
}

/// <summary>Text-to-speech engine configuration.</summary>
public sealed class TtsConfig
{
    /// <summary>Whether text-to-speech is enabled (gated by <see cref="SpeechConfig.Enabled"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// TTS engine name: "kokoro" (default, cross-platform) or "windows-sapi" (Windows only).
    /// </summary>
    public string Engine { get; set; } = "kokoro";

    /// <summary>Kokoro voice name (e.g., "af_heart"). Used when Engine = "kokoro".</summary>
    public string KokoroVoice { get; set; } = "af_heart";

    /// <summary>Path to a custom Kokoro ONNX model file. Null = auto-download default model.</summary>
    public string? KokoroModelPath { get; set; }

    /// <summary>
    /// Per-provider linear gain multipliers applied at the TTS engine level. Keys are
    /// provider names (e.g. "kokoro", "silero-v5-russian", "silero-v5-cis-base"); values
    /// are linear multipliers. Unknown/missing providers default to 1.0 (no change).
    /// Use this to normalize loudness *between* TTS engines whose raw levels differ.
    /// Per-channel knobs (e.g. <see cref="DiscordConfig.OutputGain"/>) stack on top.
    /// Saturating clamp — overshoot clips to int16 min/max rather than wrapping.
    /// </summary>
    public Dictionary<string, float> OutputGain { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kokoro"] = 1.8f,
        ["silero-v5-russian"] = 1.0f,
        ["silero-v5-cis-base"] = 1.0f,
    };

    /// <summary>Windows SAPI voice name. Used when Engine = "windows-sapi". Null = system default.</summary>
    public string? WindowsVoiceName { get; set; }

    /// <summary>Windows SAPI speech rate (-10 to 10). Used when Engine = "windows-sapi".</summary>
    public int WindowsSpeechRate { get; set; }

    /// <summary>Silero voice name (e.g., "xenia"). Used when Engine = "silero".</summary>
    public string SileroVoice { get; set; } = "xenia";

    /// <summary>Path to directory containing Silero model files. Null = default location.</summary>
    public string? SileroModelPath { get; set; }

    /// <summary>Fallback language when auto-detection fails (ISO 639-1 code). Default: "en".</summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>
    /// Per-language voice configuration. Each language maps to male + female voice references
    /// in "provider:voice" format (e.g. "silero-v5-russian:xenia", "kokoro:af_heart").
    /// </summary>
    public Dictionary<string, LanguageTtsConfig> Languages { get; set; } = new();
}

/// <summary>
/// Per-language voice configuration for composite/auto TTS mode.
/// Voice references use "provider:voice" format to identify both the provider and voice.
/// </summary>
public sealed class LanguageTtsConfig
{
    /// <summary>Male voice reference: "provider:voice" (e.g. "silero-v5-russian:aidar").</summary>
    public required string MaleVoice { get; set; }

    /// <summary>Female voice reference: "provider:voice" (e.g. "silero-v5-russian:xenia").</summary>
    public required string FemaleVoice { get; set; }
}

// ── Multi-Tenant Configuration ────────────────────────────────────────

/// <summary>
/// Configuration for a single tenant. Stored in the <c>tenants</c> section
/// of <c>cortex.yml</c>. Each tenant maps to one Agent container.
/// </summary>
public sealed class TenantConfig
{
    /// <summary>
    /// SignalR Hub endpoint URL for the tenant's Agent container. Use the IPv4 loopback
    /// (<c>127.0.0.1</c>), not <c>localhost</c>: on Windows <c>localhost</c> resolves to IPv6
    /// <c>::1</c>, which the agent's hub auth rejects as a loopback connection.
    /// Example: <c>http://127.0.0.1:5100/hub/agent</c>
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the default tenant. Only one tenant should be marked as default.
    /// The default tenant receives webchat, voice, and unmapped Discord users.
    /// </summary>
    public bool Default { get; set; }

    /// <summary>Whether the tenant is currently enabled (container should be running).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Docker container port assigned to this tenant.
    /// Managed by the Bridge when provisioning containers.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Channel IDs assigned to this tenant.
    /// Default tenant: webchat-default, voice-default, discord-dm.
    /// Non-default tenants: discord-dm, api (enforced — not user-editable).
    /// </summary>
    public List<string> Channels { get; set; } = [];

    /// <summary>
    /// Single Discord user ID linked to this tenant (null = not linked).
    /// Set automatically during the Discord pairing flow.
    /// </summary>
    public string? DiscordUserId { get; set; }

    /// <summary>
    /// Display name of the linked Discord user (e.g. "Alice#1234").
    /// Captured during pairing for display purposes only.
    /// </summary>
    public string? DiscordUsername { get; set; }

    /// <summary>
    /// The Discord guild (server) ID where this tenant's voice channel lives.
    /// Stored as string (Discord snowflake); parsed to ulong at runtime.
    /// </summary>
    public string? DiscordGuildId { get; set; }

    /// <summary>
    /// The Discord voice channel ID for real-time STT/TTS calls.
    /// When set, voice is implicitly enabled for this tenant.
    /// </summary>
    public string? DiscordVoiceChannelId { get; set; }

    /// <summary>
    /// Greeting spoken when the bot joins this tenant's voice channel.
    /// Null or empty disables the greeting.
    /// </summary>
    public string? VoiceGreeting { get; set; }

    /// <summary>
    /// Active pairing code for this tenant (e.g. "CORTEX-7X4K").
    /// Null means no pending pairing. Cleared after successful pairing or expiry.
    /// </summary>
    public string? SetupCode { get; set; }

    /// <summary>
    /// Unix timestamp (milliseconds) when the setup code expires. 0 = not set.
    /// </summary>
    public long SetupCodeExpiresAt { get; set; }

    /// <summary>
    /// Whether the API channel is enabled for this tenant (programmatic message access).
    /// </summary>
    public bool ApiEnabled { get; set; }

    // ── Legacy ────────────────────────────────────────────────────

    /// <summary>
    /// Legacy: list of Discord user IDs. Migrated to <see cref="DiscordUserId"/> at load time.
    /// Kept for backward compatibility with existing cortex.yml files.
    /// </summary>
    public List<string> DiscordUsers { get; set; } = [];

    /// <summary>
    /// Docker image version currently running for this tenant.
    /// Set by the Bridge during provisioning; used for display and rollback.
    /// </summary>
    public string? ImageVersion { get; set; }

    /// <summary>
    /// Minutes of inactivity before the container is stopped to free resources.
    /// 0 = never stop. Default: 0.
    /// </summary>
    public int IdleTimeoutMinutes { get; set; }

    /// <summary>
    /// Voice gender preference for this tenant ("female" or "male").
    /// Used by the composite TTS engine to select the appropriate voice per language.
    /// </summary>
    public string VoiceGender { get; set; } = "female";

    /// <summary>
    /// Migrates legacy <see cref="DiscordUsers"/> list to the single
    /// <see cref="DiscordUserId"/> field. Should be called once after deserialization.
    /// </summary>
    public void MigrateLegacyDiscordUsers()
    {
        if (DiscordUserId is null && DiscordUsers.Count > 0)
        {
            DiscordUserId = DiscordUsers[0];
            DiscordUsers.Clear();
        }
    }
}
