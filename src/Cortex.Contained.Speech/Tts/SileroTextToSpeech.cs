using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SileroSharp;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Russian text-to-speech using SileroSharp (Silero TTS v5 model via TorchSharp).
/// Outputs 48kHz mono 16-bit PCM audio — matches Discord's native rate, no resampling needed.
/// Thread-safe: uses a semaphore to serialize access to the synthesizer.
/// </summary>
public sealed partial class SileroTextToSpeech : ITextToSpeech
{
    /// <summary>Default Silero TorchScript model filename.</summary>
    public const string DefaultModelFileName = "silero_v5_jit.pt";

    /// <summary>Default linear gain applied to Silero output PCM. 1.0 = no change.</summary>
    public const float DefaultOutputGain = 1.0f;

    /// <summary>All Silero voices across all variants, indexed by name.</summary>
    private static readonly FrozenDictionary<string, SileroVoice> allVoicesByName =
        typeof(SileroVoice)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(SileroVoice))
            .Select(f => (SileroVoice)f.GetValue(null)!)
            .ToFrozenDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Voices available for the configured variant.</summary>
    private readonly FrozenDictionary<string, SileroVoice> voicesByName;

    private readonly ILogger<SileroTextToSpeech> logger;
    private readonly SileroTTS tts;
    private SileroVoice voice;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly float outputGain;
    private bool disposed;

    /// <summary>
    /// Creates a new Silero TTS engine. Model is loaded synchronously via TorchSharp.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="modelDir">
    /// Directory containing the model file and accentor resources.
    /// If null, uses the default path (%LOCALAPPDATA%/Cortex/models/silero/).
    /// </param>
    /// <param name="voiceName">Silero voice name (e.g. "xenia"). Default is "xenia".</param>
    /// <param name="variant">Model variant. Default is V5Russian (best quality, CC BY-NC).</param>
    /// <param name="modelFilePath">Explicit model file path. Null resolves under <paramref name="modelDir"/>.</param>
    /// <param name="outputGain">
    /// Linear gain multiplier applied to synthesized PCM. Saturating clamp — no wrap clipping.
    /// Default 1.0 (no change). Use values &gt;1 to boost a provider whose raw output is quieter
    /// than human voices on the output path.
    /// </param>
    public SileroTextToSpeech(
        ILogger<SileroTextToSpeech> logger,
        string? modelDir = null,
        string voiceName = "xenia",
        SileroModelVariant variant = SileroModelVariant.V5Russian,
        string? modelFilePath = null,
        float outputGain = DefaultOutputGain)
    {
        this.logger = logger;
        this.voicesByName = GetVoicesForVariant(variant);

        if (!float.IsFinite(outputGain) || outputGain < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(outputGain), "Must be a finite non-negative number.");
        }
        this.outputGain = outputGain;

        modelDir ??= GetDefaultModelDir();
        var modelPath = modelFilePath ?? Path.Combine(modelDir, DefaultModelFileName);
        this.voice = this.ResolveVoiceFromInstance(voiceName);

        var options = new SileroOptions { Variant = variant };
        this.tts = SileroLoader.LoadWithAutoDiscovery(modelPath, options, logger: null);

        var variantName = variant == SileroModelVariant.V5CisBase ? "v5-cis-base" : "v5-russian";
        this.LogModelLoaded(modelPath, voiceName, variantName, outputGain);
    }

    /// <summary>
    /// Internal constructor for testing (no model loading).
    /// </summary>
    internal SileroTextToSpeech(
        ILogger<SileroTextToSpeech> logger,
        SileroTTS tts,
        SileroVoice voice,
        SileroModelVariant variant = SileroModelVariant.V5Russian)
    {
        this.logger = logger;
        this.tts = tts;
        this.voice = voice;
        this.voicesByName = GetVoicesForVariant(variant);
        this.outputGain = DefaultOutputGain;
    }

    /// <summary>
    /// Returns the default model directory: %LOCALAPPDATA%/Cortex/models/silero/
    /// </summary>
    public static string GetDefaultModelDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex", "models", "silero");

    /// <inheritdoc />
    public AudioFormat OutputFormat => AudioFormat.Silero;

    /// <inheritdoc />
    public string CurrentVoice => this.voice.Name;

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <inheritdoc />
    public void SetVoice(string voiceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        ObjectDisposedException.ThrowIf(this.disposed, this);

        this.voice = this.ResolveVoiceFromInstance(voiceName);
        this.LogVoiceChanged(voiceName);
    }

    /// <inheritdoc />
    public async Task<byte[]> SynthesizeAsync(string text, string? languageHint = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = await this.tts.SynthesizeAsync(text, this.voice, cancellationToken).ConfigureAwait(false);
            var pcmBytes = chunk.ToPcm16Bytes();
            AudioConverter.ApplyGain(pcmBytes, this.outputGain);

            this.LogSynthesisComplete(text.Length, pcmBytes.Length);
            return pcmBytes;
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

        // Bounded channel: allow a few sentences to buffer ahead while playback catches up.
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var segmentIndex = 0;

        // Producer: synthesize sentences on a background task and write PCM chunks to the channel.
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in this.tts.SynthesizeStreamingAsync(text, this.voice, cancellationToken).ConfigureAwait(false))
                {
                    var pcmBytes = chunk.ToPcm16Bytes();
                    AudioConverter.ApplyGain(pcmBytes, this.outputGain);
                    var idx = Interlocked.Increment(ref segmentIndex);
                    this.LogStreamingSegmentReady(idx, pcmBytes.Length);

                    await channel.Writer.WriteAsync(pcmBytes, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation — let the channel complete gracefully.
            }
            catch (Exception ex)
            {
                this.LogStreamingError(ex.Message);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        try
        {
            // Consumer: yield PCM chunks as they arrive from the channel.
            await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }

            this.LogStreamingSynthesisComplete(text.Length, segmentIndex);
        }
        finally
        {
            // Ensure channel is completed even on cancellation (prevents deadlocked writer).
            channel.Writer.TryComplete();
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableVoices() => [.. this.voicesByName.Keys];

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.tts.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Returns all known Silero voice names across all variants.</summary>
    public static IReadOnlyList<string> GetAllKnownVoiceNames() => [.. allVoicesByName.Keys];

    /// <summary>Returns voice names for a specific variant.</summary>
    public static IReadOnlyList<string> GetVoiceNamesForVariant(SileroModelVariant variant) =>
        [.. GetVoicesForVariant(variant).Keys];

    /// <summary>Resolves a voice name to a <see cref="SileroVoice"/> instance (static, all variants).</summary>
    /// <exception cref="ArgumentException">Thrown if the voice name is not recognized.</exception>
    internal static SileroVoice ResolveVoice(string voiceName)
    {
        if (allVoicesByName.TryGetValue(voiceName, out var resolved))
        {
            return resolved;
        }

        var available = string.Join(", ", allVoicesByName.Keys.Order());
        throw new ArgumentException($"Unknown Silero voice '{voiceName}'. Available voices: {available}", nameof(voiceName));
    }

    /// <summary>
    /// Maps a config string to a <see cref="SileroModelVariant"/> enum value.
    /// </summary>
    public static SileroModelVariant ParseVariant(string? variantName) =>
        variantName?.ToLowerInvariant() switch
        {
            "v5-cis-base" or "v5cisbase" or "cis" or "mit" => SileroModelVariant.V5CisBase,
            _ => SileroModelVariant.V5Russian,
        };

    /// <summary>
    /// Returns voices filtered by model variant.
    /// V5Russian: aidar, baya, kseniya, xenia (no "ru_" prefix).
    /// V5CisBase: all "ru_" prefixed voices.
    /// </summary>
    private static FrozenDictionary<string, SileroVoice> GetVoicesForVariant(SileroModelVariant variant)
    {
        var allVoices = typeof(SileroVoice)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(SileroVoice))
            .Select(f => (SileroVoice)f.GetValue(null)!);

        var filtered = variant switch
        {
            SileroModelVariant.V5CisBase => allVoices.Where(v => v.Name.StartsWith("ru_", StringComparison.Ordinal)),
            _ => allVoices.Where(v => !v.Name.StartsWith("ru_", StringComparison.Ordinal)),
        };

        return filtered.ToFrozenDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolves a voice name within this instance's variant-filtered voices.</summary>
    private SileroVoice ResolveVoiceFromInstance(string voiceName)
    {
        if (this.voicesByName.TryGetValue(voiceName, out var resolved))
        {
            return resolved;
        }

        var available = string.Join(", ", this.voicesByName.Keys.Order());
        throw new ArgumentException(
            $"Unknown Silero voice '{voiceName}' for configured variant. Available voices: {available}",
            nameof(voiceName));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Silero TTS model loaded from {ModelPath}, voice={VoiceName}, variant={Variant}, gain={OutputGain}")]
    private partial void LogModelLoaded(string modelPath, string voiceName, string variant, float outputGain);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Silero TTS synthesis complete: {TextLength} chars -> {ByteCount} bytes PCM")]
    private partial void LogSynthesisComplete(int textLength, int byteCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Silero TTS voice changed to {VoiceName}")]
    private partial void LogVoiceChanged(string voiceName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Silero TTS streaming segment {SegmentIndex} ready: {ByteCount} bytes PCM")]
    private partial void LogStreamingSegmentReady(int segmentIndex, int byteCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Silero TTS streaming synthesis complete: {TextLength} chars, {SegmentCount} segments")]
    private partial void LogStreamingSynthesisComplete(int textLength, int segmentCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Silero TTS streaming error: {ErrorMessage}")]
    private partial void LogStreamingError(string errorMessage);
}
