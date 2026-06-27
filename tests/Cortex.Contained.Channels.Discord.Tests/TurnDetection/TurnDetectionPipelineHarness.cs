using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Stt;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace Cortex.Contained.Channels.Discord.Tests.TurnDetection;

/// <summary>
/// Deterministic replay of a fixture WAV through the production end-of-turn
/// pipeline: 20 ms RMS-VAD frames, audio accumulator, debounced Whisper
/// partials, LiveKit turn detector, <see cref="EndOfTurnDecision"/>.
/// Time advances by a virtual clock so the test runs in seconds.
/// </summary>
internal sealed class TurnDetectionPipelineHarness : IAsyncDisposable
{
    private const int SampleRate = 16_000;
    private const int FrameMs = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 320
    private const float RmsSpeechThreshold = 800f / 32_768f;       // matches DiscordVoiceHandler default
    private const int SilenceTimeoutMs = 3_000;
    private const float LowConfidenceEouThreshold = 0.005f;
    private const int MaxSilenceTimeoutMs = 6_000;
    private const int TurnDetectorDebounceMs = 250;
    private const int WhisperRefreshMs = 500; // periodic transcript refresh during silence

    private readonly WhisperFactory whisperFactory;
    private readonly WhisperProcessor whisper;
    private readonly LiveKitTurnDetector detector;
    private bool disposed;

    private TurnDetectionPipelineHarness(WhisperFactory wf, WhisperProcessor wp, LiveKitTurnDetector d)
    {
        this.whisperFactory = wf;
        this.whisper = wp;
        this.detector = d;
    }

    public static Task<TurnDetectionPipelineHarness> CreateAsync(
        string whisperModelPath,
        string turnDetectorDir,
        ILoggerFactory loggerFactory)
    {
        var wf = WhisperFactory.FromPath(whisperModelPath);
        var wp = wf.CreateBuilder().WithLanguage("en").Build();
        var d = LiveKitTurnDetector.Load(turnDetectorDir, loggerFactory);
        return Task.FromResult(new TurnDetectionPipelineHarness(wf, wp, d));
    }

    public async Task<IReadOnlyList<CommitEvent>> RunAsync(string wavPath)
    {
        var (samples, sr) = WavReader.ReadPcm16Mono(wavPath);
        if (sr != SampleRate)
        {
            throw new InvalidDataException($"Expected {SampleRate} Hz, got {sr} Hz.");
        }

        var commits = new List<CommitEvent>();
        var accumulator = new List<short>(capacity: samples.Length);
        var virtualTimeMs = 0;
        var lastSpeechTimeMs = 0;
        var hasSpeech = false;
        var lastDetectorRunMs = -10_000;
        var lastDetectorInput = string.Empty;
        var lastWhisperRunMs = -10_000;
        var lastPartialTranscript = string.Empty;
        var lastPEou = 0f;
        var threshold = this.detector.GetThreshold("en");

        for (var offset = 0; offset + FrameSamples <= samples.Length; offset += FrameSamples)
        {
            var frame = samples.AsSpan(offset, FrameSamples);
            var rms = ComputeRms(frame);
            var isSpeech = rms > RmsSpeechThreshold;

            if (isSpeech)
            {
                hasSpeech = true;
                lastSpeechTimeMs = virtualTimeMs;
                accumulator.AddRange(frame.ToArray());
            }
            else if (hasSpeech)
            {
                // Silence after speech started. Refresh transcript + detector,
                // then ask EndOfTurnDecision.
                var silenceElapsedMs = virtualTimeMs - lastSpeechTimeMs;

                if (virtualTimeMs - lastWhisperRunMs >= WhisperRefreshMs && accumulator.Count > 0)
                {
                    lastPartialTranscript = await this.TranscribeAsync(accumulator).ConfigureAwait(false);
                    lastWhisperRunMs = virtualTimeMs;
                }

                if (virtualTimeMs - lastDetectorRunMs >= TurnDetectorDebounceMs
                    && !string.IsNullOrWhiteSpace(lastPartialTranscript)
                    && !string.Equals(lastPartialTranscript, lastDetectorInput, StringComparison.Ordinal))
                {
                    lastPEou = await this.detector.PredictEndOfTurnAsync(
                        [new TurnDetectorMessage("user", lastPartialTranscript)],
                        language: "en").ConfigureAwait(false);
                    lastDetectorInput = lastPartialTranscript;
                    lastDetectorRunMs = virtualTimeMs;
                }

                var decision = EndOfTurnDecision.Decide(
                    silenceElapsedMs,
                    SilenceTimeoutMs,
                    useTurnDetector: true,
                    lastPEou,
                    threshold,
                    lowConfidenceThreshold: LowConfidenceEouThreshold,
                    maxSilenceTimeoutMs: MaxSilenceTimeoutMs);

                if (decision.Commit)
                {
                    commits.Add(new CommitEvent(
                        virtualTimeMs, decision.Reason, lastPartialTranscript, lastPEou, silenceElapsedMs));
                    accumulator.Clear();
                    hasSpeech = false;
                    lastDetectorInput = string.Empty;
                    lastPartialTranscript = string.Empty;
                    lastPEou = 0f;
                }
            }

            virtualTimeMs += FrameMs;
        }

        // Tail-flush: if speech is still pending at end-of-file, force a final
        // SilenceTimeout commit so a fixture that ends mid-speech doesn't lie.
        if (hasSpeech && accumulator.Count > 0)
        {
            var finalTranscript = await this.TranscribeAsync(accumulator).ConfigureAwait(false);
            commits.Add(new CommitEvent(
                virtualTimeMs, CommitReason.SilenceTimeout, finalTranscript, lastPEou, virtualTimeMs - lastSpeechTimeMs));
        }

        return commits;
    }

    private static float ComputeRms(ReadOnlySpan<short> frame)
    {
        long sum = 0;
        for (var i = 0; i < frame.Length; i++)
        {
            sum += frame[i] * frame[i];
        }
        var mean = sum / (double)frame.Length;
        return (float)(Math.Sqrt(mean) / 32_768.0);
    }

    private async Task<string> TranscribeAsync(List<short> pcm)
    {
        var floats = new float[pcm.Count];
        for (var i = 0; i < pcm.Count; i++)
        {
            floats[i] = pcm[i] / 32_768f;
        }
        var wavBytes = BuildWavBytes(floats, SampleRate);
        var pieces = new List<string>();
        await foreach (var seg in this.whisper.ProcessAsync(new MemoryStream(wavBytes)).ConfigureAwait(false))
        {
            pieces.Add(seg.Text);
        }
        return string.Join(" ", pieces).Trim();
    }

    /// <summary>
    /// Build a minimal RIFF/WAVE byte array (mono 16-bit PCM) from float samples.
    /// Mirrors <c>AudioConverter.BuildWavBytes</c> which is internal to the Speech project.
    /// </summary>
    private static byte[] BuildWavBytes(float[] samples, int sampleRate)
    {
        var dataSize = samples.Length * 2;
        var wavBytes = new byte[dataSize + 44];

        // RIFF header
        wavBytes[0] = (byte)'R'; wavBytes[1] = (byte)'I'; wavBytes[2] = (byte)'F'; wavBytes[3] = (byte)'F';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(4), dataSize + 36);
        wavBytes[8] = (byte)'W'; wavBytes[9] = (byte)'A'; wavBytes[10] = (byte)'V'; wavBytes[11] = (byte)'E';

        // fmt sub-chunk
        wavBytes[12] = (byte)'f'; wavBytes[13] = (byte)'m'; wavBytes[14] = (byte)'t'; wavBytes[15] = (byte)' ';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(16), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(20), 1); // PCM
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(22), 1); // mono
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(24), sampleRate);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(28), sampleRate * 2); // byteRate
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(32), 2); // blockAlign
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(34), 16); // bitsPerSample

        // data sub-chunk
        wavBytes[36] = (byte)'d'; wavBytes[37] = (byte)'a'; wavBytes[38] = (byte)'t'; wavBytes[39] = (byte)'a';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(40), dataSize);

        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            var value = (short)(clamped * short.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(44 + (i * 2)), value);
        }

        return wavBytes;
    }

    public ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return ValueTask.CompletedTask;
        }
        this.disposed = true;
        this.whisper.Dispose();
        this.whisperFactory.Dispose();
        this.detector.Dispose();
        return ValueTask.CompletedTask;
    }
}
