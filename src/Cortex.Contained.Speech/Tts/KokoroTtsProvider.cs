using System.Runtime.CompilerServices;
using KokoroSharp;
using KokoroSharp.Processing;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// TTS provider for Kokoro (English voices via KokoroSharp).
/// Outputs 24kHz mono 16-bit PCM.
/// </summary>
public sealed partial class KokoroTtsProvider : ITtsProvider
{
    private readonly KokoroTextToSpeech? engine;
    private readonly List<TtsVoiceInfo> voices;
    private readonly ILogger<KokoroTtsProvider> logger;
    private readonly string modelPath;
    private bool disposed;

    public KokoroTtsProvider(
        ILoggerFactory loggerFactory,
        string? modelPath = null,
        float outputGain = KokoroTextToSpeech.DefaultOutputGain)
    {
        this.logger = loggerFactory.CreateLogger<KokoroTtsProvider>();
        this.modelPath = modelPath ?? KokoroTextToSpeech.GetDefaultModelPath();

        this.voices = BuildVoiceMetadata();

        if (File.Exists(this.modelPath))
        {
            try
            {
                ConfigureContentPaths();
                this.engine = new KokoroTextToSpeech(
                    loggerFactory.CreateLogger<KokoroTextToSpeech>(),
                    this.modelPath,
                    outputGain: outputGain);
                this.LogProviderReady(this.modelPath, this.voices.Count);
            }
            catch (Exception ex)
            {
                this.LogProviderLoadFailed(this.modelPath, ex.Message);
                this.engine = null;
            }
        }
        else
        {
            this.LogModelNotFound(this.modelPath);
        }
    }

    /// <summary>
    /// Configures KokoroSharp to load espeak and voice data from
    /// %LOCALAPPDATA%\Cortex\models\kokoro\ instead of the application directory.
    /// This is required because the MSIX install directory is read-restricted.
    /// </summary>
    private void ConfigureContentPaths()
    {
        var kokoroContentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex", "models", "kokoro");

        var espeakDir = Path.Combine(kokoroContentDir, "espeak");
        var voicesDir = Path.Combine(kokoroContentDir, "voices");

        if (Directory.Exists(espeakDir))
        {
            Tokenizer.eSpeakNGPath = espeakDir;
            this.LogContentPathConfigured("espeak", espeakDir);
        }

        if (Directory.Exists(voicesDir))
        {
            KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
            this.LogContentPathConfigured("voices", voicesDir);
        }
    }

    /// <inheritdoc />
    public string Name => "kokoro";

    /// <inheritdoc />
    public IReadOnlyList<TtsVoiceInfo> Voices => this.voices;

    /// <inheritdoc />
    public bool IsReady => this.engine is not null;

    /// <inheritdoc />
    public string StatusDetail => this.engine is not null
        ? "Kokoro model ready"
        : $"Kokoro model not found at {this.modelPath}. Download via Settings > Speech.";

    /// <inheritdoc />
    public AudioFormat OutputFormat => AudioFormat.Kokoro;

    /// <inheritdoc />
    public bool SupportsStreaming => false;

    /// <inheritdoc />
    public async Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (this.engine is null)
        {
            throw new InvalidOperationException("Kokoro TTS engine is not ready (model not loaded).");
        }

        this.engine.SetVoice(voiceName);
        return await this.engine.SynthesizeAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
        string text, string voiceName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (this.engine is null)
        {
            throw new InvalidOperationException("Kokoro TTS engine is not ready (model not loaded).");
        }

        this.engine.SetVoice(voiceName);

        // Batch-via-yield: synthesize the whole input and yield as a single chunk.
        // Cortex now chunks at the sentence level (see SentenceChunker), so the
        // engine receives short batches and we don't need provider-side streaming.
        var pcm = await this.engine.SynthesizeAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (pcm.Length > 0)
        {
            yield return pcm;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.engine?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Known Kokoro voice names (fallback when KokoroVoiceManager is empty).</summary>
    private static readonly string[] knownVoices =
    [
        // American English
        "af_heart", "af_alloy", "af_aoede", "af_bella", "af_jessica", "af_kore",
        "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
        "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx",
        // British English
        "bf_alice", "bf_emma", "bf_isabella", "bf_lily",
        "bm_daniel", "bm_fable", "bm_george", "bm_lewis",
        // Japanese
        "jf_alpha", "jf_gongitsune", "jf_nezumi", "jf_tebukuro",
        "jm_kumo",
        // Chinese (Mandarin)
        "zf_xiaobei", "zf_xiaoni", "zf_xiaoxuan", "zf_xiaoyan",
        "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang",
        // Korean
        "kf_jieun", "kf_seonh",
        // French
        "ff_siwis",
        // Hindi
        "hf_alpha", "hm_omega",
        // Italian
        "if_sara",
        // Portuguese (Brazilian)
        "pf_dora",
    ];

    private static List<TtsVoiceInfo> BuildVoiceMetadata()
    {
        var kokoroVoices = KokoroVoiceManager.Voices;
        var voiceNames = kokoroVoices.Count > 0
            ? kokoroVoices.Select(v => v.Name).ToList()
            : knownVoices.ToList();

        var result = new List<TtsVoiceInfo>(voiceNames.Count);

        foreach (var name in voiceNames)
        {
            var (language, gender) = ParseKokoroVoiceName(name);
            var description = FormatDescription(name, language, gender);
            result.Add(new TtsVoiceInfo(name, language, gender, description));
        }

        return result;
    }

    /// <summary>
    /// Parses Kokoro voice naming convention: "af_sarah" → (en, Female).
    /// Prefix: a=American, b=British; f=Female, m=Male.
    /// </summary>
    private static (string Language, VoiceGender Gender) ParseKokoroVoiceName(string name)
    {
        var gender = name.Length >= 2 && name[1] == 'm' ? VoiceGender.Male : VoiceGender.Female;
        var language = name.Length >= 1 ? name[0] switch
        {
            'a' or 'b' => "en",
            'j' => "ja",
            'z' => "zh",
            'k' => "ko",
            'f' => "fr",
            'h' => "hi",
            'i' => "it",
            'p' => "pt",
            _ => "en",
        } : "en";
        return (language, gender);
    }

    private static string FormatDescription(string name, string language, VoiceGender gender)
    {
        var accent = name.Length >= 1 ? name[0] switch
        {
            'a' => "American",
            'b' => "British",
            'j' => "Japanese",
            'z' => "Chinese",
            'k' => "Korean",
            'f' => "French",
            'h' => "Hindi",
            'i' => "Italian",
            'p' => "Portuguese",
            _ => "English",
        } : "English";

        var rawName = name.Length > 3 ? name[3..] : name;
        var prettyName = rawName.Length > 0
            ? char.ToUpperInvariant(rawName[0]) + rawName[1..]
            : name;

        return $"{gender} — {prettyName} ({accent})";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kokoro TTS provider ready: {ModelPath}, {VoiceCount} voices")]
    private partial void LogProviderReady(string modelPath, int voiceCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kokoro TTS provider failed to load from {ModelPath}: {Error}")]
    private partial void LogProviderLoadFailed(string modelPath, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kokoro model not found at {ModelPath}")]
    private partial void LogModelNotFound(string modelPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kokoro {ContentType} path configured: {ContentPath}")]
    private partial void LogContentPathConfigured(string contentType, string contentPath);
}
