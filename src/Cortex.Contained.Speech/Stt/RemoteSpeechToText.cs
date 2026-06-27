using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Speech.Stt;

/// <summary>
/// Speech-to-text backed by the <c>whisper-stt</c> sidecar (lib/whisper-stt,
/// container <c>cortex-stt</c>). Replaces the in-process <see cref="WhisperSpeechToText"/>:
/// a hung or failed GPU inference is now an HTTP timeout/5xx, isolated from the
/// Bridge, and the model lives in the container (or, later, on the Mac) instead
/// of the host.
/// </summary>
/// <remarks>
/// Posts raw 16 kHz mono s16le PCM as the request body to
/// <c>POST /v1/transcribe</c> and <c>POST /v1/transcribe/detailed</c>; language
/// and prompt travel as query params. <see cref="CheckReadyAsync"/> reads the
/// sidecar's <c>loaded</c> flag from <c>GET /health</c>. All three Whisper
/// consumers (one-shot, streaming wrapper, Discord) depend only on
/// <see cref="ISpeechToText"/>, so swapping this in routes them all to the sidecar.
/// </remarks>
public sealed partial class RemoteSpeechToText : ISpeechToText
{
    private readonly HttpClient http;
    private readonly ILogger<RemoteSpeechToText> logger;
    // Optimistically ready (like RemoteTtsProvider) so voice consumers can wire up
    // without a blocking probe; CheckReadyAsync flips this to the live /health state.
    private volatile bool ready = true;
    private string language = "auto";

    public RemoteSpeechToText(HttpClient http, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.http = http;
        this.logger = loggerFactory.CreateLogger<RemoteSpeechToText>();
    }

    /// <inheritdoc />
    public bool IsReady => this.ready;

    /// <inheritdoc />
    public string Language => this.language;

    /// <inheritdoc />
    public void SetLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        this.language = language.Trim().ToLowerInvariant();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => supportedLanguages;

    /// <inheritdoc />
    public async Task<string?> TranscribeAsync(byte[] pcmData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pcmData);

        using var response = await this.PostPcmAsync("/v1/transcribe", pcmData, prompt: null, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            this.LogRequestFailed("/v1/transcribe", (int)response.StatusCode);
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<TranscribeDto>(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(dto?.Text) ? null : dto.Text;
    }

    /// <inheritdoc />
    public async Task<DetailedTranscription?> TranscribeDetailedAsync(
        byte[] pcmData, string? prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pcmData);

        using var response = await this.PostPcmAsync("/v1/transcribe/detailed", pcmData, prompt, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            this.LogRequestFailed("/v1/transcribe/detailed", (int)response.StatusCode);
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<DetailedDto>(cancellationToken).ConfigureAwait(false);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Text))
        {
            return null;
        }

        var tokens = (dto.Tokens ?? [])
            .Select(t => new TranscribedToken(t.Text, t.StartMs, t.EndMs))
            .ToArray();
        return new DetailedTranscription(dto.Text, tokens);
    }

    /// <summary>Polls <c>/health</c>; updates and returns the sidecar's readiness.</summary>
    public async Task<bool> CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await this.http.GetAsync("/health", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                this.ready = false;
                return false;
            }

            var health = await response.Content.ReadFromJsonAsync<HealthDto>(cancellationToken).ConfigureAwait(false);
            this.ready = health?.Loaded ?? false;
            return this.ready;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            this.ready = false;
            return false;
        }
    }

    private Task<HttpResponseMessage> PostPcmAsync(
        string path, byte[] pcm, string? prompt, CancellationToken cancellationToken)
    {
        var query = $"?language={Uri.EscapeDataString(this.language)}";
        if (!string.IsNullOrEmpty(prompt))
        {
            query += $"&prompt={Uri.EscapeDataString(prompt)}";
        }

        var content = new ByteArrayContent(pcm);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return this.http.PostAsync(path + query, content, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No-op: the HttpClient is owned by IHttpClientFactory / DI.
    }

    private sealed record TranscribeDto(
        [property: JsonPropertyName("text")] string? Text);

    private sealed record TokenDto(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("startMs")] int StartMs,
        [property: JsonPropertyName("endMs")] int EndMs);

    private sealed record DetailedDto(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("tokens")] List<TokenDto>? Tokens);

    private sealed record HealthDto(
        [property: JsonPropertyName("loaded")] bool Loaded);

    [LoggerMessage(Level = LogLevel.Warning, Message = "whisper-stt {Path} failed: HTTP {StatusCode}")]
    private partial void LogRequestFailed(string path, int statusCode);

    /// <summary>Whisper language codes; "auto" enables detection. Mirrors the
    /// list the in-process engine exposed so consumers see the same set.</summary>
    private static readonly string[] supportedLanguages =
    [
        "auto",
        "af", "am", "ar", "as", "az", "ba", "be", "bg", "bn", "bo",
        "br", "bs", "ca", "cs", "cy", "da", "de", "el", "en", "es",
        "et", "eu", "fa", "fi", "fo", "fr", "gl", "gu", "ha", "haw",
        "he", "hi", "hr", "ht", "hu", "hy", "id", "is", "it", "ja",
        "jw", "ka", "kk", "km", "kn", "ko", "la", "lb", "ln", "lo",
        "lt", "lv", "mg", "mi", "mk", "ml", "mn", "mr", "ms", "mt",
        "my", "ne", "nl", "nn", "no", "oc", "pa", "pl", "ps", "pt",
        "ro", "ru", "sa", "sd", "si", "sk", "sl", "sn", "so", "sq",
        "sr", "su", "sv", "sw", "ta", "te", "tg", "th", "tk", "tl",
        "tr", "tt", "uk", "ur", "uz", "vi", "yi", "yo", "zh",
    ];
}
