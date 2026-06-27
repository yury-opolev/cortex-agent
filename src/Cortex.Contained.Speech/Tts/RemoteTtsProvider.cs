using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// TTS provider backed by the unified <c>uni-voices</c> GPU sidecar
/// (lib/uni-voices). One instance per engine (kokoro / roest-da /
/// silero-v5-russian), all pointed at the same base URL; each sends its engine
/// name in every request. Replaces the in-process KokoroSharp/Silero/Røst
/// providers — a hung or failed GPU inference is now an HTTP timeout/5xx,
/// isolated from the Bridge process.
/// </summary>
/// <remarks>
/// Talks to <c>POST /v1/synthesize/stream</c> which returns chunked little-endian
/// int16 mono PCM at the canonical 48 kHz. <see cref="CheckReadyAsync"/> reads
/// this engine's <c>loaded</c> flag from <c>GET /health</c>; when the sidecar is
/// down or the engine is still warming, <see cref="IsReady"/> is false and
/// <see cref="CompositeTtsEngine"/> falls back to the default-language voice.
/// </remarks>
public sealed partial class RemoteTtsProvider : ITtsProvider
{
    private readonly string engineName;
    private readonly HttpClient http;
    private readonly ILogger<RemoteTtsProvider> logger;
    private readonly List<TtsVoiceInfo> voices;
    // Optimistically ready so CompositeTtsEngine routes language→engine without a
    // separate probe; a failed/just-down sidecar simply yields no audio for that
    // sentence (no crash). CheckReadyAsync flips this to the live /health state
    // when a readiness probe is wired.
    private volatile bool ready = true;

    public RemoteTtsProvider(
        string engineName,
        HttpClient http,
        ILoggerFactory loggerFactory,
        IReadOnlyList<TtsVoiceInfo> voices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineName);
        this.engineName = engineName;
        this.http = http;
        this.logger = loggerFactory.CreateLogger<RemoteTtsProvider>();
        this.voices = [.. voices];
    }

    /// <inheritdoc />
    public string Name => this.engineName;

    /// <inheritdoc />
    public IReadOnlyList<TtsVoiceInfo> Voices => this.voices;

    /// <inheritdoc />
    public bool IsReady => this.ready;

    /// <inheritdoc />
    public string StatusDetail => this.ready
        ? $"uni-voices engine '{this.engineName}' ready"
        : $"uni-voices engine '{this.engineName}' not reachable/loaded";

    /// <inheritdoc />
    public AudioFormat OutputFormat => AudioFormat.Silero; // canonical 48 kHz mono 16-bit

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <summary>Polls <c>/health</c>; updates and returns this engine's readiness.</summary>
    public async Task<bool> CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await this.http.GetAsync("/health", cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                this.ready = false;
                return false;
            }

            var health = await resp.Content
                .ReadFromJsonAsync<HealthDto>(cancellationToken)
                .ConfigureAwait(false);
            this.ready = health?.Engines is { } engines
                && engines.TryGetValue(this.engineName, out var engine)
                && engine.Loaded;
            return this.ready;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            this.ready = false;
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> SynthesizeAsync(
        string text, string voiceName, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await foreach (var chunk in this.SynthesizeStreamingAsync(text, voiceName, cancellationToken).ConfigureAwait(false))
        {
            buffer.Write(chunk);
        }
        return buffer.ToArray();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
        string text, string voiceName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/synthesize/stream")
        {
            Content = JsonContent.Create(new SynthesizeDto(this.engineName, voiceName, text, 48000)),
        };

        using var resp = await this.http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            this.LogSynthFailed((int)resp.StatusCode, this.engineName, voiceName);
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        byte[]? carry = null; // 0 or 1 leftover byte to keep 16-bit sample alignment
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            var total = (carry?.Length ?? 0) + read;
            var combined = new byte[total];
            var offset = 0;
            if (carry is not null)
            {
                combined[0] = carry[0];
                offset = 1;
                carry = null;
            }

            Array.Copy(buffer, 0, combined, offset, read);

            var even = total - (total % 2);
            if (even > 0)
            {
                yield return combined[..even];
            }

            if (total % 2 == 1)
            {
                carry = [combined[total - 1]];
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No-op: the HttpClient is owned by IHttpClientFactory / DI.
    }

    private sealed record SynthesizeDto(
        [property: JsonPropertyName("engine")] string Engine,
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("sampleRate")] int SampleRate);

    private sealed record EngineHealthDto(
        [property: JsonPropertyName("loaded")] bool Loaded);

    private sealed record HealthDto(
        [property: JsonPropertyName("engines")] Dictionary<string, EngineHealthDto>? Engines);

    [LoggerMessage(Level = LogLevel.Warning, Message = "uni-voices /v1/synthesize/stream failed: HTTP {StatusCode} (engine={Engine}, voice={Voice})")]
    private partial void LogSynthFailed(int statusCode, string engine, string voice);
}
