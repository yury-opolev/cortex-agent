using System.Globalization;
using System.Text;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Shared formatting helpers for memory tool results.
/// </summary>
internal static class MemoryFormatHelper
{
    /// <summary>
    /// Formats a <see cref="MemoryResult"/> into a human-readable string
    /// suitable for returning to the LLM.
    /// </summary>
    public static string FormatMemoryResult(MemoryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"ID: {result.MemoryId}");
        if (result.Title is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Title: {result.Title}");
        }

        if (result.Tags.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Tags: {string.Join(", ", result.Tags)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Created: {result.CreatedAt:u}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Updated: {result.UpdatedAt:u}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Content:\n{result.Content}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Checks whether the embedding service (Ollama) is available and the model is pulled.
    /// Returns a descriptive error message if not ready, or <c>null</c> if everything is OK.
    /// </summary>
    public static async Task<string?> CheckEmbeddingAvailabilityAsync(
        IEmbeddingService embeddingService, CancellationToken cancellationToken)
    {
        if (embeddingService is not OllamaEmbeddingService ollama)
        {
            return null; // Non-Ollama implementation; assume available
        }

        if (!await ollama.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return "Memory service is not available: Ollama is not reachable. "
                + "It may still be starting up — please try again in a moment.";
        }

        if (!await ollama.IsModelAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return "Memory service is not available: the embedding model is still being downloaded. "
                + "Please try again in a minute or two.";
        }

        return null;
    }
}
