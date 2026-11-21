using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// <see cref="IReranker"/> over llama.cpp <c>llama-server --reranking</c> (<c>POST /v1/rerank</c>,
/// Jina request/response shape, <c>Authorization: Bearer</c>). Every call re-checks
/// <see cref="RerankerEndpointGuard"/>, so a runtime endpoint change can never leak text, and nothing
/// of the request or response body is logged or put into exception messages.
/// </summary>
public sealed class LlamaServerReranker : IReranker
{
    /// <summary>Named <see cref="HttpClient"/> (registered without request logging).</summary>
    public const string HttpClientName = "reranker";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptionsMonitor<RerankerOptions> options;
    private readonly Func<string?> apiKey;

    public LlamaServerReranker(IHttpClientFactory httpClientFactory, IOptionsMonitor<RerankerOptions> options, RerankerAccess access)
        : this(httpClientFactory, options, () => access?.ApiKey)
    {
        ArgumentNullException.ThrowIfNull(access);
    }

    /// <summary>Uses an explicit key source (evaluation and smoke CLIs read it from the environment).</summary>
    public LlamaServerReranker(IHttpClientFactory httpClientFactory, IOptionsMonitor<RerankerOptions> options, Func<string?> apiKey)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.apiKey = apiKey;
    }

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            var settings = this.options.CurrentValue;
            return RerankerEndpointGuard.Check(settings.Endpoint, this.apiKey(), settings.AllowedHosts());
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            return [];
        }

        var settings = this.options.CurrentValue;
        var key = this.apiKey();
        if (RerankerEndpointGuard.Check(settings.Endpoint, key, settings.AllowedHosts()) is { } reason)
        {
            throw new RerankerException("Text was not sent to the reranker: " + reason + ".");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Endpoint.TrimEnd('/') + "/v1/rerank"))
        {
            Content = JsonContent.Create(new RerankRequest(settings.Model, query, documents, documents.Count)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var client = this.httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RerankerException("The reranker is unreachable.", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new RerankerException("The reranker rejected the API key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RerankerException($"The reranker answered HTTP {(int)response.StatusCode}.");
            }

            RerankResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<RerankResponse>(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new RerankerException("The reranker returned malformed JSON.", ex);
            }

            return Validate(body, documents.Count);
        }
    }

    /// <summary>
    /// <c>GET /health</c> (public in llama-server, no key sent): true once the model is loaded. Never throws
    /// for transport errors.
    /// </summary>
    public async Task<bool> ProbeHealthAsync(CancellationToken cancellationToken)
    {
        var settings = this.options.CurrentValue;
        try
        {
            using var response = await this.httpClientFactory.CreateClient(HttpClientName)
                .GetAsync(new Uri(settings.Endpoint.TrimEnd('/') + "/health"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static List<RerankScore> Validate(RerankResponse? body, int count)
    {
        if (body?.Results is not { } results)
        {
            throw new RerankerException("The reranker response has no results.");
        }

        var seen = new HashSet<int>();
        var scores = new List<RerankScore>(results.Count);
        foreach (var result in results)
        {
            if (result.Index < 0 || result.Index >= count || !seen.Add(result.Index) || !double.IsFinite(result.RelevanceScore))
            {
                throw new RerankerException("The reranker response has an invalid or duplicate index or score.");
            }

            scores.Add(new RerankScore(result.Index, result.RelevanceScore));
        }

        return scores;
    }

    private sealed record RerankRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents,
        [property: JsonPropertyName("top_n")] int TopN);

    private sealed record RerankResponse([property: JsonPropertyName("results")] IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("relevance_score")] double RelevanceScore);
}
