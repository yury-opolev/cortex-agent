using System.Text.Json;

namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Pure mapping logic shared by every embedding-endpoint probe (the Bridge-side
/// <c>EmbeddingProbe</c> and the agent-side <c>AgentHub.TestEmbeddingEndpoint</c>).
/// Keeping the request body, the response parsing, and the status-to-result mapping
/// in one place guarantees both sites report identical results — no subtly different
/// duplicated logic.
/// </summary>
public static class EmbeddingProbeMapper
{
    /// <summary>The path appended to the endpoint base URL for the embed probe.</summary>
    public const string EmbedPath = "/api/embed";

    /// <summary>The single-token input used to exercise the embedding model.</summary>
    public const string ProbeInput = "ping";

    /// <summary>Builds the JSON request body for an embed probe against the given model.</summary>
    public static string BuildRequestBody(string model)
        => JsonSerializer.Serialize(new { model, input = ProbeInput });

    /// <summary>Builds the full probe URL from an endpoint base and the embed path.</summary>
    public static string BuildProbeUrl(string endpoint)
        => endpoint.TrimEnd('/') + EmbedPath;

    /// <summary>The result returned when the endpoint could not be reached (connect failure / timeout).</summary>
    public static EmbeddingProbeResult Unreachable()
        => new() { Ok = false, Dim = null, Error = "Endpoint unreachable" };

    /// <summary>
    /// Maps an HTTP response to a probe result.
    /// <list type="bullet">
    /// <item>401/403 → "Unauthorized (bad or missing API key)"</item>
    /// <item>other non-2xx → "HTTP {code}"</item>
    /// <item>2xx with unparsable body → "Invalid response format"</item>
    /// <item>2xx with dim ≠ expected → "Dimension mismatch (expected {n})"</item>
    /// <item>2xx with the expected dim → Ok</item>
    /// </list>
    /// </summary>
    /// <param name="statusCode">The HTTP status code of the probe response.</param>
    /// <param name="body">The response body (read only for 2xx responses; may be null otherwise).</param>
    /// <param name="expectedDimensions">The embedding dimension the model is expected to return.</param>
    public static EmbeddingProbeResult MapResponse(int statusCode, string? body, int expectedDimensions)
    {
        if (statusCode is 401 or 403)
        {
            return new EmbeddingProbeResult { Ok = false, Dim = null, Error = "Unauthorized (bad or missing API key)" };
        }

        if (statusCode is < 200 or >= 300)
        {
            return new EmbeddingProbeResult { Ok = false, Dim = null, Error = $"HTTP {statusCode}" };
        }

        var dim = ParseDimension(body);

        if (dim is null)
        {
            return new EmbeddingProbeResult { Ok = false, Dim = null, Error = "Invalid response format" };
        }

        if (dim != expectedDimensions)
        {
            return new EmbeddingProbeResult { Ok = false, Dim = dim, Error = $"Dimension mismatch (expected {expectedDimensions})" };
        }

        return new EmbeddingProbeResult { Ok = true, Dim = dim, Error = null };
    }

    /// <summary>
    /// Parses the embedding dimension from an Ollama <c>/api/embed</c> response body
    /// of the shape <c>{"embeddings":[[...]]}</c>. Returns null when the body is null,
    /// malformed, or does not contain a non-empty embeddings array.
    /// </summary>
    public static int? ParseDimension(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("embeddings", out var embeddingsEl)
                && embeddingsEl.ValueKind == JsonValueKind.Array
                && embeddingsEl.GetArrayLength() > 0)
            {
                var first = embeddingsEl[0];
                if (first.ValueKind == JsonValueKind.Array)
                {
                    return first.GetArrayLength();
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — return null below
        }

        return null;
    }
}
