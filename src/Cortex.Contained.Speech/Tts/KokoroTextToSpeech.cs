using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Cross-platform text-to-speech using KokoroSharp (Kokoro 82M model).
/// Outputs 24kHz mono 16-bit PCM audio.
/// Thread-safe: uses a semaphore to serialize access to the synthesizer.
/// </summary>
public sealed partial class KokoroTextToSpeech : ITextToSpeech
{
    /// <summary>Default Kokoro model filename.</summary>
    public const string DefaultModelFileName = "kokoro-v1.0.onnx";

    /// <summary>Download URL for the default Kokoro ONNX model (~326 MB).</summary>
    public const string DefaultModelDownloadUrl = "https://github.com/taylorchu/kokoro-onnx/releases/download/v0.2.0/kokoro.onnx";

    /// <summary>Default linear gain applied to Kokoro output PCM. 1.0 = no change.</summary>
    public const float DefaultOutputGain = 1.0f;

    private readonly ILogger<KokoroTextToSpeech> logger;
    private readonly KokoroWavSynthesizer synthesizer;
    private KokoroVoice voice;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly float outputGain;
    private bool disposed;

    /// <summary>
    /// Pipeline config for streaming synthesis. Uses smaller segment sizes than the default
    /// to reduce time-to-first-audio (default is 510/510 which effectively disables segmentation).
    /// </summary>
    private static readonly KokoroTTSPipelineConfig StreamingPipelineConfig = new(
        new DefaultSegmentationConfig
        {
            MinFirstSegmentLength = 10,
            MaxFirstSegmentLength = 80,
            MaxSecondSegmentLength = 100,
            MinFollowupSegmentsLength = 150,
        });

    /// <summary>
    /// Pipeline config for batch synthesis — defaults with large max so the whole text
    /// fits in a single segment (no internal chunking).
    /// </summary>
    private static readonly KokoroTTSPipelineConfig BatchPipelineConfig = new(
        new DefaultSegmentationConfig
        {
            MaxFirstSegmentLength = 510,
            MaxSecondSegmentLength = 510,
        });

    /// <summary>
    /// Creates a new Kokoro TTS engine. Model is loaded synchronously.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="modelPath">
    /// Path to the ONNX model file. If null, uses the default path
    /// (%LOCALAPPDATA%/Cortex/models/kokoro-v1.0.onnx). The model must already
    /// be downloaded — use the settings page to download it.
    /// </param>
    /// <param name="voiceName">Kokoro voice name (e.g. "af_heart"). Default is "af_heart".</param>
    /// <param name="outputGain">
    /// Linear gain multiplier applied to synthesized PCM. Kokoro's raw output peaks well below
    /// digital full-scale, so a gain of ~1.5–2.0 (+3.5 to +6 dB) typically matches the loudness
    /// of human voices on Discord. Clamped saturating — no wrap-around clipping. Default 1.0.
    /// </param>
    public KokoroTextToSpeech(
        ILogger<KokoroTextToSpeech> logger,
        string? modelPath = null,
        string voiceName = "af_heart",
        float outputGain = DefaultOutputGain)
    {
        this.logger = logger;

        if (!float.IsFinite(outputGain) || outputGain < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(outputGain), "Must be a finite non-negative number.");
        }
        this.outputGain = outputGain;

        modelPath ??= GetDefaultModelPath();
        this.synthesizer = new KokoroWavSynthesizer(modelPath, this.CreateSessionOptions());
        this.voice = KokoroVoiceManager.GetVoice(voiceName);

        this.LogModelLoaded(modelPath, voiceName, outputGain);
    }

    /// <summary>
    /// Builds the ONNX Runtime <see cref="SessionOptions"/> for the Kokoro model.
    /// On Windows the DirectML execution provider is appended so inference runs on
    /// the GPU — Kokoro on CPU synthesizes slower than real-time, which starves the
    /// voice-out pipeline and produces audible inter-sentence gaps. DirectML is
    /// CUDA-independent, so it does not clash with Whisper.net's CUDA runtime.
    /// If DirectML cannot initialize (no DX12 device, missing runtime), or on a
    /// non-Windows host, synthesis falls back to the CPU execution provider with a
    /// loud warning so a degraded backend can't hide behind slow audio for days.
    /// </summary>
    private SessionOptions CreateSessionOptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SessionOptions();
        }

        try
        {
            var options = new SessionOptions();
            // DirectML requires sequential execution with memory pattern disabled.
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(0);
            this.LogGpuExecutionProvider();
            return options;
        }
        catch (Exception ex)
        {
            this.LogGpuUnavailable(ex.Message);
            return new SessionOptions();
        }
    }

    /// <summary>
    /// Returns the default model path: %LOCALAPPDATA%/Cortex/models/kokoro-v1.0.onnx
    /// </summary>
    public static string GetDefaultModelPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex", "models", DefaultModelFileName);

    /// <summary>
    /// Internal constructor for testing.
    /// </summary>
    internal KokoroTextToSpeech(ILogger<KokoroTextToSpeech> logger, KokoroWavSynthesizer synthesizer, KokoroVoice voice)
    {
        this.logger = logger;
        this.synthesizer = synthesizer;
        this.voice = voice;
        this.outputGain = DefaultOutputGain;
    }

    /// <inheritdoc />
    public AudioFormat OutputFormat => AudioFormat.Kokoro;

    /// <inheritdoc />
    public string CurrentVoice => this.voice.Name;

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <inheritdoc />
    public void SetVoice(string voiceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var newVoice = KokoroVoiceManager.GetVoice(voiceName);
        this.voice = newVoice;
        this.LogVoiceChanged(voiceName);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Bypasses <see cref="KokoroWavSynthesizer.SynthesizeAsync"/> and drives the engine
    /// via <see cref="KokoroEngine.EnqueueJob"/> directly so that each step's callback is
    /// assigned <em>before</em> the job is visible to the worker thread. KokoroSharp's
    /// own entry points enqueue first and assign callbacks after, which loses the race on
    /// a warmed-up engine (inference finishes before the callback is set — samples get
    /// silently discarded and the result is an empty PCM buffer).
    /// </remarks>
    public async Task<byte[]> SynthesizeAsync(string text, string? languageHint = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pcmBytes = await RunJobToBytesAsync(text, BatchPipelineConfig, cancellationToken).ConfigureAwait(false);
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
    /// <remarks>
    /// Same pattern as <see cref="SynthesizeAsync"/> — build the job, wire OnStepComplete
    /// callbacks first, then enqueue. Streams each step's PCM through a bounded channel
    /// so the caller can yield segments as they're ready without blocking Kokoro's
    /// inference thread.
    /// </remarks>
    public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
        string text, string? languageHint = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Bounded channel: allow a few segments to buffer ahead while playback catches up.
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var segmentIndex = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pipelineConfig = StreamingPipelineConfig;
            var tokens = Tokenizer.Tokenize(text.Trim(), this.voice.GetLangCode(), pipelineConfig.PreprocessText);
            var segments = pipelineConfig.SegmentationFunc(tokens);
            var job = KokoroJob.Create(segments, this.voice, pipelineConfig.Speed, OnComplete: null);

            // Wire callbacks BEFORE enqueue — see race-condition note on SynthesizeAsync.
            for (var i = 0; i < job.Steps.Count; i++)
            {
                var step = job.Steps[i];
                var isFinalStep = i == job.Steps.Count - 1;
                step.OnStepComplete = samples =>
                {
                    var processed = KokoroPlayback.PostProcessSamples(samples);
                    var pcmBytes = KokoroPlayback.GetBytes(processed);
                    AudioConverter.ApplyGain(pcmBytes, this.outputGain);
                    var idx = Interlocked.Increment(ref segmentIndex);
                    this.LogStreamingSegmentReady(idx, pcmBytes.Length);

                    // If the consumer abandoned the stream (cancellation, barge-in), the
                    // channel is completed and WriteAsync throws ChannelClosedException.
                    // KokoroSharp runs this callback on its own worker thread with no
                    // top-level handler, so an uncaught throw kills the process — drop.
                    if (!TryWriteOrDrop(channel.Writer, pcmBytes))
                    {
                        this.LogStreamingSegmentDropped(idx);
                    }

                    if (isFinalStep)
                    {
                        channel.Writer.TryComplete();
                    }
                };
            }

            this.synthesizer.EnqueueJob(job);

            // If the segmenter returned no segments (empty text after tokenization),
            // close the channel so the consumer doesn't wait forever.
            if (job.Steps.Count == 0)
            {
                channel.Writer.TryComplete();
            }

            // Yield segments as they arrive.
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

    /// <summary>
    /// Runs a Kokoro synthesis job end-to-end and returns the concatenated PCM bytes.
    /// Mirrors <see cref="KokoroWavSynthesizer.SynthesizeAsync"/> but — critically —
    /// sets every step's <c>OnStepComplete</c> callback <em>before</em> enqueuing the
    /// job. KokoroSharp's own implementation enqueues first and assigns callbacks after,
    /// which loses the race on a warmed-up engine.
    /// </summary>
    private async Task<byte[]> RunJobToBytesAsync(string text, KokoroTTSPipelineConfig pipelineConfig, CancellationToken ct)
    {
        var tokens = Tokenizer.Tokenize(text.Trim(), this.voice.GetLangCode(), pipelineConfig.PreprocessText);
        var segments = pipelineConfig.SegmentationFunc(tokens);
        var job = KokoroJob.Create(segments, this.voice, pipelineConfig.Speed, OnComplete: null);

        var bytes = new List<byte>();
        var bytesLock = new Lock();

        foreach (var step in job.Steps)
        {
            var currentStep = step;
            step.OnStepComplete = samples =>
            {
                var processed = KokoroPlayback.PostProcessSamples(samples);
                var pcmBytes = KokoroPlayback.GetBytes(processed);
                lock (bytesLock)
                {
                    bytes.AddRange(pcmBytes);

                    // Inter-segment pauses: only add padding when the step ended on a
                    // punctuation token (matches KokoroSharp's own batch behavior).
                    var lastToken = currentStep.Tokens.Length > 0 ? currentStep.Tokens[^1] : -1;
                    if (lastToken >= 0 && Tokenizer.PunctuationTokens.Contains(lastToken))
                    {
                        var punctChar = Tokenizer.TokenToChar[lastToken];
                        var secondsToWait = pipelineConfig.SecondsOfPauseBetweenProperSegments[punctChar];
                        var silenceSampleCount = (int)(secondsToWait * KokoroPlayback.waveFormat.SampleRate);
                        if (silenceSampleCount > 0)
                        {
                            bytes.AddRange(KokoroPlayback.GetBytes(new float[silenceSampleCount]));
                        }
                    }
                }
            };
        }

        this.synthesizer.EnqueueJob(job);

        // Empty-segmentation guard: if there's no work, return immediately.
        if (job.Steps.Count == 0)
        {
            return [];
        }

        while (!job.isDone)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct).ConfigureAwait(false);
        }

        lock (bytesLock)
        {
            return bytes.ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableVoices()
    {
        var voices = KokoroVoiceManager.Voices;
        var names = new List<string>(voices.Count);
        foreach (var voice in voices)
        {
            names.Add(voice.Name);
        }

        return names;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.synthesizer.Dispose();
        this.gate.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kokoro TTS model loaded from {ModelPath}, voice={VoiceName}, gain={OutputGain}")]
    private partial void LogModelLoaded(string modelPath, string voiceName, float outputGain);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kokoro TTS backend: DirectML GPU active")]
    private partial void LogGpuExecutionProvider();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kokoro TTS backend DEGRADED: DirectML GPU unavailable, falling back to CPU ({Error}). Synthesis will be slower than real-time and cause inter-sentence gaps in voice output.")]
    private partial void LogGpuUnavailable(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kokoro TTS batch synthesis complete: {TextLength} chars -> {ByteCount} bytes PCM")]
    private partial void LogSynthesisComplete(int textLength, int byteCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kokoro TTS voice changed to {VoiceName}")]
    private partial void LogVoiceChanged(string voiceName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kokoro TTS streaming segment {SegmentIndex} ready: {ByteCount} bytes PCM")]
    private partial void LogStreamingSegmentReady(int segmentIndex, int byteCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kokoro TTS streaming synthesis complete: {TextLength} chars, {SegmentCount} segments")]
    private partial void LogStreamingSynthesisComplete(int textLength, int segmentCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kokoro TTS streaming segment {SegmentIndex} dropped (consumer closed channel)")]
    private partial void LogStreamingSegmentDropped(int segmentIndex);

    /// <summary>
    /// Writes a PCM chunk to the channel, swallowing <see cref="ChannelClosedException"/>
    /// if the consumer has already abandoned the stream. Returns <c>true</c> if the chunk
    /// was written, <c>false</c> if the channel was already closed.
    /// </summary>
    /// <remarks>
    /// KokoroSharp invokes its <c>OnProgress</c> callback on an internal worker thread with
    /// no top-level exception handler — an uncaught exception terminates the whole process.
    /// The consumer side may complete the channel on cancellation/barge-in before all
    /// segments have been produced, so writes after that must be dropped silently.
    /// </remarks>
    internal static bool TryWriteOrDrop(ChannelWriter<byte[]> writer, byte[] chunk)
    {
        try
        {
            writer.WriteAsync(chunk, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }
}
