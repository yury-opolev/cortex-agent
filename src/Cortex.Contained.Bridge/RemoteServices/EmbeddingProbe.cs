using System.Net;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.RemoteServices;

/// <summary>
/// Probes an Ollama-compatible embedding endpoint by sending a small embed request and
/// validating the response dimension. The pure mapping (request body, response parsing,
/// status → result) lives in <see cref="EmbeddingProbeMapper"/> and is shared with the
/// agent-side probe so both report identical results.
/// </summary>
/// <remarks>
/// The production "Test connection" path now runs on the AGENT
/// (<c>IMemoryHub.TestEmbeddingEndpoint</c>) so it tests from the same Docker network
/// where embeddings actually run. This Bridge-side probe is retained for direct
/// host-context probing and unit coverage of the shared mapping.
/// </remarks>
internal static class EmbeddingProbe
{
    private const string EmbeddingModel = "qwen3-embedding:0.6b";
    private const int ExpectedDimensions = 1024;

    /// <summary>
    /// Probes <paramref name="endpoint"/> by POSTing to <c>{endpoint}/api/embed</c>.
    /// Returns an <see cref="EmbeddingProbeResult"/> describing whether the probe succeeded.
    /// </summary>
    /// <param name="endpoint">Base URL of the embedding service (e.g. <c>http://mac:11434</c>).</param>
    /// <param name="apiKey">Optional Bearer token. Null/empty = no Authorization header.</param>
    /// <param name="http">HTTP client to use for the probe request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<EmbeddingProbeResult> ProbeAsync(
        string endpoint,
        string? apiKey,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var url = EmbeddingProbeMapper.BuildProbeUrl(endpoint);
        var requestBody = EmbeddingProbeMapper.BuildRequestBody(EmbeddingModel);

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

            return EmbeddingProbeMapper.MapResponse((int)response.StatusCode, body, ExpectedDimensions);
        }
    }

    /// <summary>
    /// Parses the embedding dimension from an Ollama <c>/api/embed</c> response body.
    /// Returns null when the body cannot be parsed. Delegates to the shared mapper.
    /// </summary>
    internal static int? ParseDimension(string body) => EmbeddingProbeMapper.ParseDimension(body);
}
