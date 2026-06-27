using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>
/// Sends an embed probe request to an endpoint and maps the result. Runs inside the
/// agent container so it tests from the same network context where embeddings actually
/// run (Docker-internal names like <c>http://embeddings:11434</c> resolve here, but not
/// on the Bridge host). The pure request/response mapping is shared with the Bridge-side
/// probe via <see cref="EmbeddingProbeMapper"/> so both report identical results.
/// </summary>
internal static class EmbeddingEndpointProber
{
    /// <summary>
    /// Probes <paramref name="endpoint"/> by POSTing to <c>{endpoint}/api/embed</c> with
    /// the given model, and validates the response against <paramref name="expectedDimensions"/>.
    /// </summary>
    /// <param name="endpoint">Base URL of the embedding service.</param>
    /// <param name="apiKey">Optional Bearer token; null/empty means no Authorization header.</param>
    /// <param name="model">Embedding model id to request.</param>
    /// <param name="expectedDimensions">Expected embedding dimension.</param>
    /// <param name="http">HTTP client to use (caller owns its lifetime/timeout).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<EmbeddingProbeResult> ProbeAsync(
        string endpoint,
        string? apiKey,
        string model,
        int expectedDimensions,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var url = EmbeddingProbeMapper.BuildProbeUrl(endpoint);
        var requestBody = EmbeddingProbeMapper.BuildRequestBody(model);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return EmbeddingProbeMapper.Unreachable();
        }

        using (response)
        {
            string? body = null;
            if (response.IsSuccessStatusCode)
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return EmbeddingProbeMapper.MapResponse((int)response.StatusCode, body, expectedDimensions);
        }
    }
}
