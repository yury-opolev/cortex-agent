namespace Cortex.Contained.Agent.Host.Llm;

/// <summary>
/// Resolves the reasoning-effort value actually sent to a provider.
/// <para>
/// The two provider families disagree on both the wire shape and the accepted levels, so the
/// requested level is resolved per family and per model rather than passed through. Sending the
/// parameter to a model that does not accept it is an HTTP 400, and so is sending a level the
/// model does not offer — resolving to <see langword="null"/> means "send nothing and let the
/// provider apply its own default".
/// </para>
/// <para>
/// OpenAI Responses carries it as <c>reasoning.effort</c> and tops out at <c>high</c>. Anthropic
/// carries it as <c>output_config.effort</c> behind a beta header, only a subset of Claude 4
/// models accept it, and only Opus offers <c>max</c>.
/// </para>
/// </summary>
internal static class LlmReasoningEffort
{
    /// <summary>The beta header value gating Anthropic's <c>output_config.effort</c>.</summary>
    internal const string AnthropicBetaHeader = "effort-2025-11-24";

    private static readonly string[] Levels = ["minimal", "low", "medium", "high", "max"];

    /// <summary>True when <paramref name="value"/> names a known effort level.</summary>
    internal static bool IsLevel(string? value) => Normalize(value) is not null;

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        return Array.IndexOf(Levels, trimmed) >= 0 ? trimmed : null;
    }

    /// <summary>
    /// The value for the Responses API's <c>reasoning.effort</c>, or <see langword="null"/> to
    /// send nothing. <c>max</c> clamps to <c>high</c> because it is an Anthropic-only level.
    /// </summary>
    internal static string? ResolveForResponses(string model, string? requested)
    {
        _ = model;
        var level = Normalize(requested);
        if (level is null)
        {
            return null;
        }

        return level == "max" ? "high" : level;
    }

    /// <summary>
    /// The value for Anthropic's <c>output_config.effort</c>, or <see langword="null"/> to send
    /// nothing (including for every model that does not accept the parameter).
    /// </summary>
    internal static string? ResolveForAnthropic(string model, string? requested)
    {
        var level = Normalize(requested);
        if (level is null || !AnthropicModelSupportsEffort(model))
        {
            return null;
        }

        // "minimal" is an OpenAI level; Anthropic's floor is "low".
        if (level == "minimal")
        {
            return "low";
        }

        // Only Opus offers "max" — degrade rather than fail on the others.
        if (level == "max" && !AnthropicModelSupportsMaxEffort(model))
        {
            return "high";
        }

        return level;
    }

    /// <summary>The Claude 4 models that accept the effort parameter.</summary>
    private static bool AnthropicModelSupportsEffort(string? model)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();
        return m.Contains("opus-4-8", StringComparison.Ordinal)
            || m.Contains("opus-4-6", StringComparison.Ordinal)
            || m.Contains("sonnet-4-6", StringComparison.Ordinal);
    }

    private static bool AnthropicModelSupportsMaxEffort(string? model)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();
        return m.Contains("opus-4-8", StringComparison.Ordinal)
            || m.Contains("opus-4-6", StringComparison.Ordinal);
    }
}
