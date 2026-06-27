using System.ComponentModel.DataAnnotations;

namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// Controls how images are aged out of the LLM context window.
/// When a message is older than the Nth-from-end user message (where N =
/// <see cref="PreserveRecentTurns"/>), any image content blocks in it are
/// replaced with text at LLM prep time.
/// </summary>
public sealed class ImageAgingConfig
{
    /// <summary>
    /// Recent user turns whose messages (and any assistant / tool messages that
    /// follow each of them) keep their images intact. Counted by user-role
    /// messages only — assistant and tool messages do not consume the budget.
    /// Messages older than the Nth-from-end user message have their images
    /// replaced (either with an LLM-generated description or with a plain
    /// "[Image removed]" placeholder depending on <see cref="DescribeOnStrip"/>).
    /// Set to 0 to disable image aging entirely (never strip).
    /// </summary>
    [Range(0, 100)]
    public int PreserveRecentTurns { get; set; } = 4;

    /// <summary>
    /// When true, stripped images are replaced with an LLM-generated text
    /// description produced by <c>IImageDescriber</c> (using the Memory model slot).
    /// When false, they are replaced with "[Image removed: {mediaType}]".
    /// </summary>
    public bool DescribeOnStrip { get; set; } = true;
}
