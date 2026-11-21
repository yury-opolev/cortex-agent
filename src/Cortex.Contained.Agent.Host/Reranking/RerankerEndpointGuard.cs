namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// Reranker exposure rule (§4.3, same spirit as the history embedder guard): candidate text goes only
/// to the in-compose sidecar (a host in <see cref="RerankerOptions.AllowedEndpointHosts"/>) or to a
/// remote endpoint over HTTPS, and <b>always</b> with an API key — the sidecar requires authentication.
/// </summary>
public static class RerankerEndpointGuard
{
    /// <summary>Returns why text must not be sent to <paramref name="endpoint"/>, or null when it may.</summary>
    public static string? Check(string? endpoint, string? apiKey, IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return "the reranker endpoint is not a valid URL";
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return "no reranker API key (the Bridge provides it; the sidecar requires authentication)";
        }

        if (allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"reranker host '{uri.Host}' is not an allowed in-compose sidecar ({string.Join(", ", allowedHosts)}); a remote reranker needs HTTPS";
    }
}
