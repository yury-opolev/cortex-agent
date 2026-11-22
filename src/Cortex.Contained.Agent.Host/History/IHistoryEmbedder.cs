namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// The embedder used for history chunks and queries (§4.2). Implementations must only send history
/// text to an allowed endpoint (<see cref="UnavailableReason"/>).
/// </summary>
public interface IHistoryEmbedder
{
    /// <summary>Identifies the vector space (model, dimensions, context window); part of the index fingerprint.</summary>
    string Fingerprint { get; }

    /// <summary>Why history text must not be sent to the embedder now, or null when it may.</summary>
    string? UnavailableReason { get; }

    /// <summary>Embeds chunk texts (no instruction prefix); one vector per text, in order.</summary>
    Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);

    /// <summary>Embeds a search query (with the configured instruction prefix, if any).</summary>
    Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken);
}
