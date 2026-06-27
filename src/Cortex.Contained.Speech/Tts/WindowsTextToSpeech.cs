using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Synthesis;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Text-to-speech using Windows built-in <see cref="SpeechSynthesizer"/>.
/// Output is 16kHz mono 16-bit PCM (resampled from SAPI's native output).
/// Windows-only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsTextToSpeech : ITextToSpeech
{
    private readonly ILogger<WindowsTextToSpeech> logger;
    private readonly SpeechSynthesizer synthesizer;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public WindowsTextToSpeech(ILogger<WindowsTextToSpeech> logger, string? voiceName = null, int rate = 0)
    {
        this.logger = logger;
        this.synthesizer = new SpeechSynthesizer();

        if (!string.IsNullOrEmpty(voiceName))
        {
            this.synthesizer.SelectVoice(voiceName);
            this.LogVoiceSelected(voiceName);
        }

        this.synthesizer.Rate = Math.Clamp(rate, -10, 10);
    }

    /// <inheritdoc />
    public string CurrentVoice => this.synthesizer.Voice.Name;

    /// <inheritdoc />
    public AudioFormat OutputFormat => AudioFormat.Whisper; // 16kHz mono 16-bit (we resample to this)

    /// <inheritdoc />
    public async Task<byte[]> SynthesizeAsync(string text, string? languageHint = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // SpeechSynthesizer outputs to a MemoryStream as WAV, then we extract raw PCM
            using var outputStream = new MemoryStream();
            this.synthesizer.SetOutputToWaveStream(outputStream);
            this.synthesizer.Speak(text);

            outputStream.Position = 0;

            var pcmData = ExtractAndResample(outputStream);
            this.LogSynthesisComplete(text.Length, pcmData.Length);
            return pcmData;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableVoices()
    {
#pragma warning disable CA1304 // GetInstalledVoices without CultureInfo intentionally returns all voices
        var voices = this.synthesizer.GetInstalledVoices();
#pragma warning restore CA1304
        var names = new List<string>(voices.Count);

        foreach (var voice in voices)
        {
            if (voice.Enabled)
            {
                names.Add(voice.VoiceInfo.Name);
            }
        }

        return names;
    }

    /// <inheritdoc />
    public void SetVoice(string voiceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        ObjectDisposedException.ThrowIf(this.disposed, this);
        this.synthesizer.SelectVoice(voiceName);
        this.LogVoiceSelected(voiceName);
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

    /// <summary>
    /// Read WAV stream, skip header, extract raw PCM, and resample to 16kHz if needed.
    /// Simple WAV parser — SAPI always outputs standard PCM WAV.
    /// </summary>
    private static byte[] ExtractAndResample(MemoryStream wavStream)
    {
        var wavBytes = wavStream.ToArray();

        // Parse WAV header to get format info
        if (wavBytes.Length < 44)
        {
            return [];
        }

        var sourceSampleRate = BitConverter.ToInt32(wavBytes, 24);
        var bitsPerSample = BitConverter.ToInt16(wavBytes, 34);
        var channels = BitConverter.ToInt16(wavBytes, 22);

        // Find data chunk
        var dataOffset = 44; // Standard WAV header
        var dataSize = wavBytes.Length - dataOffset;

        if (dataSize <= 0)
        {
            return [];
        }

        var pcmData = wavBytes.AsSpan(dataOffset, dataSize);

        // If stereo, convert to mono first (average channels)
        if (channels == 2 && bitsPerSample == 16)
        {
            var monoSamples = pcmData.Length / 4; // 2 channels * 2 bytes per sample
            var mono = new byte[monoSamples * 2];
            for (var i = 0; i < monoSamples; i++)
            {
                var left = BitConverter.ToInt16(wavBytes, dataOffset + i * 4);
                var right = BitConverter.ToInt16(wavBytes, dataOffset + i * 4 + 2);
                var avg = (short)((left + right) / 2);
                BitConverter.TryWriteBytes(mono.AsSpan(i * 2), avg);
            }

            return AudioConverter.Resample(mono, sourceSampleRate, AudioFormat.Whisper.SampleRate);
        }

        return AudioConverter.Resample(pcmData, sourceSampleRate, AudioFormat.Whisper.SampleRate);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "TTS voice selected: {VoiceName}")]
    private partial void LogVoiceSelected(string voiceName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TTS synthesis complete: {TextLength} chars -> {ByteCount} bytes PCM")]
    private partial void LogSynthesisComplete(int textLength, int byteCount);
}
