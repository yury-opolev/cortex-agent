namespace Cortex.Contained.Evals.Setup;

/// <summary>
/// Configuration for the eval LLM provider. Stored at
/// <c>%LOCALAPPDATA%\Cortex\eval.yml</c>, separate from production credentials.
/// API keys are encrypted via DPAPI in <c>eval-secrets.json</c>.
/// </summary>
public sealed class EvalConfig
{
    /// <summary>LLM provider name (e.g. "anthropic", "openai").</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>API type (e.g. "anthropic-messages", "openai-completions").</summary>
    public string Api { get; set; } = "openai-completions";

    /// <summary>Optional base URL override (null = use provider default).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Model to use for eval (judge + user simulation).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Whether eval credentials have been configured.</summary>
    public bool IsConfigured { get; set; }
}
