namespace Cortex.Contained.Speech.SpeakerId;

using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

/// <summary>
/// <see cref="ISpeakerEmbedder"/> backed by the voice-id sidecar service.
/// Each <see cref="EmbedAsync"/> call requests a fresh <see cref="HttpClient"/>
/// from the injected <see cref="IHttpClientFactory"/>, so handler-pool rotation
/// and DNS refresh happen as the factory intends.
/// </summary>
public sealed class HttpSpeakerEmbedder : ISpeakerEmbedder
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly string httpClientName;
    private readonly int sampleRate;

    public string ModelId { get; }
    public int EmbeddingDim { get; }

    public HttpSpeakerEmbedder(IHttpClientFactory httpClientFactory, string httpClientName, string modelId, int embeddingDim, int sampleRate)
    {
        this.httpClientFactory = httpClientFactory;
        this.httpClientName = httpClientName;
        this.ModelId = modelId;
        this.EmbeddingDim = embeddingDim;
        this.sampleRate = sampleRate;
    }

    public async ValueTask<float[]> EmbedAsync(ReadOnlyMemory<short> pcm16Mono16k, CancellationToken cancellationToken)
    {
        var bytes = MemoryMarshal.AsBytes(pcm16Mono16k.Span).ToArray();
        var req = new Request { SampleRate = this.sampleRate, Pcm16Base64 = Convert.ToBase64String(bytes) };

        var http = this.httpClientFactory.CreateClient(this.httpClientName);
        using var response = await http.PostAsJsonAsync("/embed", req, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<Response>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sidecar returned empty body.");
        return body.Embedding;
    }

    private sealed class Request
    {
        [JsonPropertyName("sampleRate")] public int SampleRate { get; init; }
        [JsonPropertyName("pcm16Base64")] public string Pcm16Base64 { get; init; } = "";
    }

    private sealed class Response
    {
        [JsonPropertyName("embedding")] public float[] Embedding { get; init; } = [];
        [JsonPropertyName("embeddingDim")] public int EmbeddingDim { get; init; }
        [JsonPropertyName("modelId")] public string ModelId { get; init; } = "";
    }
}
