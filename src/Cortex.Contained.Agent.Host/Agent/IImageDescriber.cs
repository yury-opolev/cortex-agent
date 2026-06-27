namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Produces a textual description of an image, used by <see cref="ContextManager"/>
/// to replace aged-out image content blocks with text. Implementations are expected
/// to be resilient: return null on any failure so the caller can fall back to a
/// plain "[Image removed]" placeholder without breaking the LLM turn.
/// </summary>
public interface IImageDescriber
{
    /// <summary>
    /// Describe the image in 1-3 sentences. Used for stale images that are being
    /// aged out of the context window — brief text is enough because the user
    /// is no longer referring to them. Returns null on any failure (timeout,
    /// provider error, empty response, no vision support, etc.).
    /// </summary>
    /// <param name="imageData">Raw image bytes.</param>
    /// <param name="mediaType">MIME type, e.g. "image/png".</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<string?> DescribeAsync(
        byte[] imageData,
        string mediaType,
        CancellationToken ct);

    /// <summary>
    /// Describe the image in detail (5-8 sentences: subject, visible text/OCR,
    /// counts, colors, layout, anything a user might reasonably ask about).
    /// Used during emergency compaction for images belonging to recent user
    /// turns — the user may still be actively referring to them so the agent
    /// needs enough text content to reason from after the image bytes are gone.
    /// Returns null on any failure.
    /// </summary>
    ValueTask<string?> DescribeVerboseAsync(
        byte[] imageData,
        string mediaType,
        CancellationToken ct);
}
