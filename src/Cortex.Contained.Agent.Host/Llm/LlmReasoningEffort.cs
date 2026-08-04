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
    /// send nothing. Gated on the model: the parameter is only legal on reasoning models, and
    /// sending it elsewhere is an HTTP 400 that the failover policy treats as terminal.
    /// <c>max</c> clamps to <c>high</c> (an Anthropic-only level) and <c>minimal</c> clamps to
    /// <c>low</c> outside the gpt-5 family that offers it.
    /// </summary>
    internal static string? ResolveForResponses(string model, string? requested)
    {
        var level = Normalize(requested);
        if (level is null || !ResponsesModelSupportsEffort(model))
        {
            return null;
        }

        if (level == "max")
        {
            return "high";
        }

        if (level == "minimal" && !IsGpt5Family(model))
        {
            return "low";
        }

        return level;
    }

    /// <summary>
    /// Reasoning families served over the Responses API. Deliberately a allow-list: a model we do
    /// not recognise gets no reasoning parameter rather than a turn-killing 400.
    /// </summary>
    private static bool ResponsesModelSupportsEffort(string? model)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();
        return IsGpt5Family(m)
            || m.Contains("codex", StringComparison.Ordinal)
            || StartsWithOSeries(m);
    }

    private static bool IsGpt5Family(string? model)
        => (model ?? string.Empty).ToLowerInvariant().Contains("gpt-5", StringComparison.Ordinal);

    /// <summary>OpenAI's o-series reasoning models (o1, o3, o4-mini, ...).</summary>
    private static bool StartsWithOSeries(string model)
    {
        if (model.Length < 2 || model[0] != 'o' || !char.IsAsciiDigit(model[1]))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The value for Anthropic's <c>output_config.effort</c>, or <see langword="null"/> to send
    /// nothing (including for every model or surface that does not accept the parameter).
    /// </summary>
    /// <param name="providerApi">
    /// The provider's API type. GitHub Copilot serves Claude over the Messages shape but never
    /// receives an <c>anthropic-beta</c> header, and <c>output_config.effort</c> without its
    /// gating beta is an HTTP 400 — so effort is never sent there. Resolving the value and the
    /// header from this single decision keeps the body and the gate from ever disagreeing.
    /// </param>
    internal static string? ResolveForAnthropic(string providerApi, string model, string? requested)
    {
        if (string.Equals(providerApi, "github-copilot-api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

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

    /// <summary>
    /// The Claude 4 models that accept the effort parameter. Matches both the hyphenated
    /// (<c>opus-4-8</c>) and dotted (<c>opus-4.8</c>) ID forms, which different catalogs use for
    /// the same model.
    /// </summary>
    private static bool AnthropicModelSupportsEffort(string? model)
        => MatchesAny(model, ["opus-4-8", "opus-4-6", "sonnet-4-6"]);

    private static bool AnthropicModelSupportsMaxEffort(string? model)
        => MatchesAny(model, ["opus-4-8", "opus-4-6"]);

    /// <summary>Substring match that treats '.' and '-' as equivalent version separators.</summary>
    private static bool MatchesAny(string? model, string[] hyphenatedNeedles)
    {
        var m = (model ?? string.Empty).ToLowerInvariant().Replace('.', '-');
        foreach (var needle in hyphenatedNeedles)
        {
            if (m.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
