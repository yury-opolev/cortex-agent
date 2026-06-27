namespace Cortex.Contained.Bridge.RemoteServices;

/// <summary>
/// Resolves a remote-service endpoint to use: the user override when set, else the
/// local container default. Single source of truth for the local defaults so the
/// UI, the push path, and the test-connection probe all agree.
/// </summary>
public sealed class RemoteServiceResolver
{
    /// <summary>Local (in-Docker) default endpoint for the embeddings sidecar.</summary>
    public const string EmbeddingsLocalDefault = "http://embeddings:11434";

    /// <summary>The endpoint to actually use: trimmed override, or the local default when blank.</summary>
    public string EffectiveEmbeddingEndpoint(string? overrideEndpoint)
        => string.IsNullOrWhiteSpace(overrideEndpoint) ? EmbeddingsLocalDefault : overrideEndpoint.Trim();

    /// <summary>True when the effective endpoint is the local default (drives the UI "Local (default)" badge).</summary>
    public bool IsEmbeddingDefault(string? endpoint)
        => string.Equals(this.EffectiveEmbeddingEndpoint(endpoint), EmbeddingsLocalDefault, StringComparison.OrdinalIgnoreCase);
}
