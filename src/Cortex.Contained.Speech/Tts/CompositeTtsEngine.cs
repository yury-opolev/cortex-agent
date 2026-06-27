using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Lingua;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Multi-language TTS engine that detects input language and routes to the
/// appropriate <see cref="ITtsProvider"/>. Each language maps to a provider
/// + voice selected by the configured <see cref="VoiceGender"/>.
/// Output is always normalized to 48kHz mono 16-bit PCM.
/// </summary>
public sealed partial class CompositeTtsEngine : ITextToSpeech
{
    /// <summary>Maps ISO 639-1 codes to Lingua Language enum values.</summary>
    private static readonly FrozenDictionary<string, Language> linguaLanguageMap =
        new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase)
        {
            ["ru"] = Language.Russian,
            ["en"] = Language.English,
            ["de"] = Language.German,
            ["fr"] = Language.French,
            ["es"] = Language.Spanish,
            ["it"] = Language.Italian,
            ["uk"] = Language.Ukrainian,
            ["pl"] = Language.Polish,
            ["pt"] = Language.Portuguese,
            ["zh"] = Language.Chinese,
            ["ja"] = Language.Japanese,
            ["ko"] = Language.Korean,
            ["kk"] = Language.Kazakh,
            ["da"] = Language.Danish,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reverse: Lingua Language → ISO 639-1 code.</summary>
    private static readonly FrozenDictionary<Language, string> reverseLinguaMap =
        linguaLanguageMap.ToFrozenDictionary(kv => kv.Value, kv => kv.Key);

    private FrozenDictionary<string, ResolvedVoice> languageVoiceMap;
    private readonly IReadOnlyList<ITtsProvider> providers;
    private string defaultLanguage;
    private LanguageDetector detector;
    private VoiceGender gender;
    private readonly ILogger<CompositeTtsEngine> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    /// <summary>
    /// Creates a composite TTS engine from providers and configuration.
    /// </summary>
    /// <param name="providers">All available TTS providers.</param>
    /// <param name="defaultLanguage">Fallback ISO 639-1 language code.</param>
    /// <param name="gender">Voice gender preference.</param>
    /// <param name="languageOverrides">
    /// Optional explicit language → (providerName, maleVoice, femaleVoice) overrides.
    /// If null, voices are auto-resolved from provider metadata.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public CompositeTtsEngine(
        IReadOnlyList<ITtsProvider> providers,
        string defaultLanguage,
        VoiceGender gender,
        Dictionary<string, LanguageVoiceConfig>? languageConfigs,
        ILogger<CompositeTtsEngine> logger)
    {
        this.providers = providers;
        this.defaultLanguage = defaultLanguage;
        this.gender = gender;
        this.logger = logger;

        // Build language → voice map and detector
        this.languageVoiceMap = BuildLanguageVoiceMap(providers, gender, languageConfigs);
        this.detector = BuildDetector(this.languageVoiceMap);

        var genderName = gender == VoiceGender.Male ? "male" : "female";
        this.LogEngineCreated(this.languageVoiceMap.Count, defaultLanguage, genderName);
        this.LogRoutingDiagnostics(languageConfigs, genderName);
    }

    /// <summary>
    /// Hot-reloads the language → voice configuration without restarting.
    /// Called when the user saves language settings via the API.
    /// Thread-safe: acquires the synthesis gate to prevent mid-synthesis changes.
    /// </summary>
    public async Task ReloadLanguageConfigAsync(
        string defaultLanguage,
        VoiceGender gender,
        Dictionary<string, LanguageVoiceConfig>? languageConfigs,
        CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.defaultLanguage = defaultLanguage;
            this.gender = gender;
            this.languageVoiceMap = BuildLanguageVoiceMap(this.providers, gender, languageConfigs);
            this.detector = BuildDetector(this.languageVoiceMap);

            var genderName = gender == VoiceGender.Male ? "male" : "female";
            this.LogConfigReloaded(this.languageVoiceMap.Count, defaultLanguage, genderName);
            this.LogRoutingDiagnostics(languageConfigs, genderName);
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Always 48kHz mono 16-bit. Underlying engines are resampled if needed.</remarks>
    public AudioFormat OutputFormat => AudioFormat.Silero; // 48kHz

    /// <inheritdoc />
    public string CurrentVoice =>
        this.languageVoiceMap.TryGetValue(this.defaultLanguage, out var resolved)
            ? resolved.VoiceName
            : string.Empty;

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <inheritdoc />
    public async Task<byte[]> SynthesizeAsync(string text, string? languageHint = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (provider, voiceName, language) = this.Resolve(text, languageHint);
            var pcm = await SynthesizeOneAsync(provider, voiceName, cancellationToken).ConfigureAwait(false);

            var hinted = !string.IsNullOrEmpty(languageHint) && string.Equals(language, languageHint, StringComparison.Ordinal);
            this.LogRouted(language, provider.Name, voiceName, text.Length, hinted);

            // A resolved engine that returned nothing (e.g. the sidecar 5xx'd) would
            // otherwise leave the user with silence. Retry once on the default-language
            // voice — unless we were already on it.
            if (pcm.Length == 0
                && !string.Equals(language, this.defaultLanguage, StringComparison.OrdinalIgnoreCase)
                && this.languageVoiceMap.TryGetValue(this.defaultLanguage, out var fallback)
                && fallback.Provider.IsReady)
            {
                this.LogSynthFallback(language, this.defaultLanguage, fallback.Provider.Name);
                pcm = await SynthesizeOneAsync(fallback.Provider, fallback.VoiceName, cancellationToken).ConfigureAwait(false);
            }

            return pcm;

            async Task<byte[]> SynthesizeOneAsync(ITtsProvider p, string voice, CancellationToken ct)
            {
                var audio = await p.SynthesizeAsync(text, voice, ct).ConfigureAwait(false);
                if (p.OutputFormat.SampleRate != AudioFormat.Silero.SampleRate)
                {
                    audio = AudioConverter.Resample(audio, p.OutputFormat.SampleRate, AudioFormat.Silero.SampleRate);
                }

                return audio;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
        string text, string? languageHint = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (provider, voiceName, language) = this.Resolve(text, languageHint);
            var hinted = !string.IsNullOrEmpty(languageHint) && string.Equals(language, languageHint, StringComparison.Ordinal);

            this.LogStreamingRouted(language, provider.Name, voiceName, text.Length, hinted);

            // Yield chunks as they arrive so first audio starts ASAP (low latency on the
            // healthy path). Only fall back if the primary stream emitted NOTHING — a
            // partial-then-fail is not "zero audio", and re-synthesizing would double-play
            // the prefix already yielded, so the emittedAny flag suppresses fallback then.
            var emittedAny = false;
            await foreach (var chunk in StreamResampledAsync(provider, voiceName, cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Length > 0)
                {
                    emittedAny = true;
                }

                yield return chunk;
            }

            if (!emittedAny
                && !string.Equals(language, this.defaultLanguage, StringComparison.OrdinalIgnoreCase)
                && this.languageVoiceMap.TryGetValue(this.defaultLanguage, out var fallback)
                && fallback.Provider.IsReady)
            {
                this.LogSynthFallback(language, this.defaultLanguage, fallback.Provider.Name);
                await foreach (var chunk in StreamResampledAsync(fallback.Provider, fallback.VoiceName, cancellationToken).ConfigureAwait(false))
                {
                    yield return chunk;
                }
            }

            async IAsyncEnumerable<byte[]> StreamResampledAsync(
                ITtsProvider p, string voice, [EnumeratorCancellation] CancellationToken ct)
            {
                var needsResample = p.OutputFormat.SampleRate != AudioFormat.Silero.SampleRate;
                var sourceSampleRate = p.OutputFormat.SampleRate;
                await foreach (var chunk in p.SynthesizeStreamingAsync(text, voice, ct).ConfigureAwait(false))
                {
                    yield return needsResample
                        ? AudioConverter.Resample(chunk, sourceSampleRate, AudioFormat.Silero.SampleRate)
                        : chunk;
                }
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableVoices()
    {
        var voices = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in this.providers)
        {
            foreach (var voice in provider.Voices)
            {
                if (seen.Add(voice.Name))
                {
                    voices.Add(voice.Name);
                }
            }
        }

        return voices;
    }

    /// <inheritdoc />
    public void SetVoice(string voiceName)
    {
        // In composite mode, voice is resolved by language + gender.
        // SetVoice is a no-op — voice routing is automatic.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        var disposedProviders = new HashSet<ITtsProvider>(ReferenceEqualityComparer.Instance);
        foreach (var provider in this.providers)
        {
            if (disposedProviders.Add(provider))
            {
                provider.Dispose();
            }
        }

        this.gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Resolves the provider + voice for the given text, optionally short-circuiting
    /// Lingua detection when a valid <paramref name="languageHint"/> is provided.
    /// </summary>
    private (ITtsProvider Provider, string VoiceName, string Language) Resolve(string text, string? languageHint = null)
    {
        var language = this.ResolveLanguage(text, languageHint);

        // Readiness is checked LIVE here, not baked into the map: a configured
        // provider that is still warming up (e.g. the Danish container) falls
        // back to the default-language voice, and the very next call routes to
        // it once it reports ready — no map rebuild needed.
        if (this.languageVoiceMap.TryGetValue(language, out var resolved) && resolved.Provider.IsReady)
        {
            return (resolved.Provider, resolved.VoiceName, language);
        }

        // Fallback to default
        resolved = this.languageVoiceMap[this.defaultLanguage];
        return (resolved.Provider, resolved.VoiceName, this.defaultLanguage);
    }

    /// <summary>
    /// Script-first per-sentence language selection. A sentence whose dominant
    /// script maps to exactly one configured language wins deterministically,
    /// overriding a (possibly stale) sticky <paramref name="languageHint"/> — this
    /// is what lets a bilingual reply route correctly sentence-by-sentence. A
    /// shared-script sentence trusts the sticky hint; with no hint it falls back
    /// to statistical detection.
    /// </summary>
    private string ResolveLanguage(string text, string? languageHint)
    {
        var dominant = ScriptDetector.DetectDominantScript(text);
        if (dominant != TextScript.None)
        {
            string? only = null;
            var count = 0;
            foreach (var code in this.languageVoiceMap.Keys)
            {
                if (ScriptDetector.ScriptOf(code) == dominant)
                {
                    only = code;
                    count++;
                }
            }

            if (count == 1 && only is not null)
            {
                return only;
            }
        }

        // A hint is only trustworthy when its script matches the sentence's own
        // script. A stale cross-script hint (e.g. "ru" on Latin text) is
        // discarded so detection — not a poisoned sticky language — decides.
        if (!string.IsNullOrEmpty(languageHint)
            && this.languageVoiceMap.ContainsKey(languageHint)
            && (dominant == TextScript.None || ScriptDetector.ScriptOf(languageHint) == dominant))
        {
            return languageHint;
        }

        return this.DetectLanguage(text);
    }

    private string DetectLanguage(string text)
    {
        var detected = this.detector.DetectLanguageOf(text);
        if (detected == Language.Unknown)
        {
            return this.defaultLanguage;
        }

        return reverseLinguaMap.TryGetValue(detected, out var code) ? code : this.defaultLanguage;
    }

    /// <summary>
    /// Builds the language → (provider, voiceName) map.
    /// If language configs are provided, parses "provider:voice" references.
    /// Otherwise auto-resolves from provider voice metadata by gender.
    /// </summary>
    private static FrozenDictionary<string, ResolvedVoice> BuildLanguageVoiceMap(
        IReadOnlyList<ITtsProvider> providers,
        VoiceGender gender,
        Dictionary<string, LanguageVoiceConfig>? languageConfigs)
    {
        var map = new Dictionary<string, ResolvedVoice>(StringComparer.OrdinalIgnoreCase);
        var providersByName = providers.ToFrozenDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        if (languageConfigs is { Count: > 0 })
        {
            // Explicit config: parse "provider:voice" references
            foreach (var (lang, config) in languageConfigs)
            {
                var voiceRef = gender == VoiceGender.Male ? config.MaleVoice : config.FemaleVoice;
                var (providerName, voiceName) = LanguageVoiceConfig.ParseVoiceReference(voiceRef);

                // Map a configured language to its provider whenever the provider
                // EXISTS by name, regardless of current readiness. Readiness is a
                // dynamic concern resolved live in Resolve(), not a build-time
                // snapshot — otherwise a provider that starts not-ready (Danish
                // container) would never be mapped even after it comes up.
                if (providersByName.TryGetValue(providerName, out var provider))
                {
                    map[lang] = new ResolvedVoice(provider, voiceName);
                }
            }
        }
        else
        {
            // Auto-resolve from provider voice metadata
            foreach (var provider in providers.Where(p => p.IsReady))
            {
                foreach (var voice in provider.Voices)
                {
                    if (voice.Gender != gender)
                    {
                        continue;
                    }

                    // First matching voice per language wins
                    if (!map.ContainsKey(voice.Language))
                    {
                        map[voice.Language] = new ResolvedVoice(provider, voice.Name);
                    }
                }
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Builds a Lingua detector restricted to the mapped languages.</summary>
    private static LanguageDetector BuildDetector(FrozenDictionary<string, ResolvedVoice> languageVoiceMap)
    {
        var linguaLanguages = languageVoiceMap.Keys
            .Where(code => linguaLanguageMap.ContainsKey(code))
            .Select(code => linguaLanguageMap[code])
            .ToArray();

        return linguaLanguages.Length >= 2
            ? LanguageDetectorBuilder.FromLanguages(linguaLanguages).Build()
            : LanguageDetectorBuilder.FromAllLanguages().Build();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Composite TTS engine: {LanguageCount} languages, default={DefaultLanguage}, gender={Gender}")]
    private partial void LogEngineCreated(int languageCount, string defaultLanguage, string gender);

    [LoggerMessage(Level = LogLevel.Information, Message = "Composite TTS config reloaded: {LanguageCount} languages, default={DefaultLanguage}, gender={Gender}")]
    private partial void LogConfigReloaded(int languageCount, string defaultLanguage, string gender);

    // Routing decisions are Information-level so they're visible in the default
    // log without flipping to Debug — essential for diagnosing language/voice
    // mis-routing in the field.
    [LoggerMessage(Level = LogLevel.Information, Message = "TTS routed: lang={Language} → {Provider}/{Voice} ({TextLength} chars, hinted={Hinted})")]
    private partial void LogRouted(string language, string provider, string voice, int textLength, bool hinted);

    [LoggerMessage(Level = LogLevel.Information, Message = "TTS streaming: lang={Language} → {Provider}/{Voice} ({TextLength} chars, hinted={Hinted})")]
    private partial void LogStreamingRouted(string language, string provider, string voice, int textLength, bool hinted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TTS produced no audio for lang={FromLanguage}; falling back to default lang={ToLanguage} ({Provider})")]
    private partial void LogSynthFallback(string fromLanguage, string toLanguage, string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "TTS routing table (gender={Gender}, default={DefaultLanguage}, {Count} langs): {Table}")]
    private partial void LogRoutingTable(string gender, string defaultLanguage, int count, string table);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TTS language '{Lang}' configured but NOT routable for gender {Gender} (voice '{VoiceRef}' did not resolve to a known provider); text in this language falls back to the default voice")]
    private partial void LogLanguageUnroutable(string lang, string voiceRef, string gender);

    /// <summary>
    /// Logs the full resolved routing table (language → provider/voice for the
    /// active gender) plus a warning for any configured language that did not
    /// resolve to a known provider. Called on construction and on every reload
    /// so the live state is always visible in the log.
    /// </summary>
    private void LogRoutingDiagnostics(Dictionary<string, LanguageVoiceConfig>? languageConfigs, string genderName)
    {
        var table = this.languageVoiceMap.Count == 0
            ? "(none — all text uses the default fallback voice)"
            : string.Join(", ", this.languageVoiceMap.Select(kv => $"{kv.Key}->{kv.Value.Provider.Name}/{kv.Value.VoiceName}"));
        this.LogRoutingTable(genderName, this.defaultLanguage, this.languageVoiceMap.Count, table);

        if (languageConfigs is { Count: > 0 })
        {
            foreach (var (lang, cfg) in languageConfigs)
            {
                if (!this.languageVoiceMap.ContainsKey(lang))
                {
                    var voiceRef = this.gender == VoiceGender.Male ? cfg.MaleVoice : cfg.FemaleVoice;
                    this.LogLanguageUnroutable(lang, string.IsNullOrWhiteSpace(voiceRef) ? "(empty)" : voiceRef, genderName);
                }
            }
        }
    }
}
