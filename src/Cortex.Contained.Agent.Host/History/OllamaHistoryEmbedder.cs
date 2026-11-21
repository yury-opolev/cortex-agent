using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Embedder exposure rule (§4.2): history text goes only to the in-compose embeddings sidecar
/// (a host in <see cref="HistoryDenseOptions.AllowedEndpointHosts"/>), or to a remote endpoint that
/// uses HTTPS and an API key.
/// </summary>
public static class HistoryEmbedderGuard
{
    /// <summary>Returns why history text must not be sent to <paramref name="endpoint"/>, or null when it may.</summary>
    public static string? Check(string? endpoint, string? apiKey, IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return "the embedding endpoint is not a valid URL";
        }

        if (allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrEmpty(apiKey) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return $"embedding host '{uri.Host}' is not an allowed in-compose sidecar ({string.Join(", ", allowedHosts)}); a remote embedder needs HTTPS and an API key";
        }

        return null;
    }
}

/// <summary>
/// <see cref="IHistoryEmbedder"/> over the shared MemoryMcp <see cref="IEmbeddingService"/> (Ollama,
/// Qwen3-Embedding). Documents are embedded as they are; queries get the configured Qwen3 instruction.
/// Every call re-checks <see cref="HistoryEmbedderGuard"/>, so a runtime endpoint change can never leak text.
/// </summary>
public sealed class OllamaHistoryEmbedder : IHistoryEmbedder
{
    private readonly IEmbeddingService embeddings;
    private readonly IOptionsMonitor<MemoryMcpOptions> memoryOptions;
    private readonly IOptionsMonitor<HistoryOptions> historyOptions;

    public OllamaHistoryEmbedder(
        IEmbeddingService embeddings,
        IOptionsMonitor<MemoryMcpOptions> memoryOptions,
        IOptionsMonitor<HistoryOptions> historyOptions)
    {
        this.embeddings = embeddings;
        this.memoryOptions = memoryOptions;
        this.historyOptions = historyOptions;
    }

    /// <inheritdoc />
    public string Fingerprint
    {
        get
        {
            var ollama = this.memoryOptions.CurrentValue.Ollama;
            return FormattableString.Invariant($"{ollama.Model};dims={ollama.Dimensions};ctx={ollama.NumCtx}");
        }
    }

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            var ollama = this.memoryOptions.CurrentValue.Ollama;
            return HistoryEmbedderGuard.Check(ollama.Endpoint, ollama.ApiKey, this.historyOptions.CurrentValue.Dense.AllowedHosts());
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        this.EnsureAllowed();
        var vectors = await this.embeddings.EmbedBatchAsync(texts, cancellationToken).ConfigureAwait(false);
        foreach (var vector in vectors)
        {
            this.EnsureDimensions(vector);
        }

        return vectors;
    }

    /// <inheritdoc />
    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        this.EnsureAllowed();
        var dense = this.historyOptions.CurrentValue.Dense;
        var vector = await this.embeddings.EmbedQueryAsync(query, dense.UseQueryInstruction ? dense.QueryInstruction : null, cancellationToken).ConfigureAwait(false);
        this.EnsureDimensions(vector);
        return vector;
    }

    private void EnsureAllowed()
    {
        if (this.UnavailableReason is { } reason)
        {
            throw new InvalidOperationException("History text was not sent to the embedder: " + reason + ".");
        }
    }

    private void EnsureDimensions(float[] vector)
    {
        var expected = this.memoryOptions.CurrentValue.Ollama.Dimensions;
        if (vector.Length != expected)
        {
            throw new InvalidOperationException($"The embedder returned {vector.Length} dimensions; {expected} are configured.");
        }
    }
}
