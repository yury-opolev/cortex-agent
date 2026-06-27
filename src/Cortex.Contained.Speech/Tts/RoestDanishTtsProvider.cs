using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Streaming TTS provider backed by the Danish Røst-v3 sidecar
/// (lib/danish-tts). Talks to POST /synthesize/stream which returns chunked
/// 16-bit mono PCM at 24 kHz. Holds no Docker knowledge — the container
/// lifecycle is owned by DanishTtsLifecycle in the Bridge; this provider only
/// needs the HTTP endpoint. When the sidecar is down, IsReady is false and
/// CompositeTtsEngine falls back to the default-language voice.
/// </summary>
public sealed partial class RoestDanishTtsProvider : ITtsProvider
{
    private readonly HttpClient http;
    private readonly ILogger<RoestDanishTtsProvider> logger;
    private readonly List<TtsVoiceInfo> voices;
    private volatile bool ready;

    public RoestDanishTtsProvider(HttpClient http, ILoggerFactory loggerFactory)
    {
        this.http = http;
        this.logger = loggerFactory.CreateLogger<RoestDanishTtsProvider>();
        // CoRal Røst-v3 speaker genders, verified empirically (2026-05-24) by
        // pitch analysis of the model's OWN reference clips: "mic" is MALE
        // (~154 Hz source / ~115 Hz synthesized), "nic" is FEMALE (~168 Hz).
        // The upstream spike README had these reversed — do not "fix" back.
        this.voices =
        [
            new TtsVoiceInfo("nic", "da", VoiceGender.Female, "Røst-v3 Danish (female)"),
            new TtsVoiceInfo("mic", "da", VoiceGender.Male, "Røst-v3 Danish (male)"),
        ];
    }

    /// <inheritdoc />
    public string Name => "roest-da";

    /// <inheritdoc />
    public IReadOnlyList<TtsVoiceInfo> Voices => this.voices;

    /// <inheritdoc />
    public bool IsReady => this.ready;

    /// <inheritdoc />
    public string StatusDetail => this.ready
        ? "Danish TTS sidecar ready"
        : "Danish TTS sidecar not reachable";

    /// <inheritdoc />
    public AudioFormat OutputFormat => AudioFormat.Kokoro; // 24 kHz mono 16-bit

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <summary>Polls /health; updates and returns readiness.</summary>
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
            this.ready = health?.ModelLoaded == true;
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
        var request = new HttpRequestMessage(HttpMethod.Post, "/synthesize/stream")
        {
            Content = JsonContent.Create(new SynthesizeDto(text, voiceName)),
        };

        using var resp = await this.http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            this.LogSynthFailed((int)resp.StatusCode, voiceName);
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

            // Yield only sample-aligned (even-length) chunks; a trailing odd byte
            // is carried forward to prepend to the next read so a 16-bit sample is
            // never split across chunks (downstream resamples each chunk).
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

        // For valid 16-bit PCM the total byte count is even, so `carry` is empty
        // here. A stray trailing byte (malformed stream) is intentionally dropped
        // rather than emitting a half-sample.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No-op: the HttpClient is owned by IHttpClientFactory / DI, not this
        // long-lived singleton provider, so we must not dispose it here. There
        // are no unmanaged resources to release.
    }

    private sealed record SynthesizeDto(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("voice")] string Voice);

    private sealed record HealthDto(
        [property: JsonPropertyName("model_loaded")] bool ModelLoaded,
        [property: JsonPropertyName("sample_rate")] int? SampleRate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Danish TTS /synthesize/stream failed: HTTP {StatusCode} (voice={Voice})")]
    private partial void LogSynthFailed(int statusCode, string voice);
}
