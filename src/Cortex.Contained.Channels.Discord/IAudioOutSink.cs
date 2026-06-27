namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Port for writing 20 ms PCM audio frames to a destination (Discord, test recorder, etc.).
/// Decouples <see cref="VoiceOutPipeline"/> from Discord.Net's <c>AudioOutStream</c>.
/// </summary>
internal interface IAudioOutSink : IAsyncDisposable
{
    /// <summary>True when frames can be written. False after disposal or transient connection loss.</summary>
    bool IsAvailable { get; }

    /// <summary>Write one 20 ms 48 kHz stereo 16-bit PCM frame (3840 bytes).</summary>
    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);

    /// <summary>Flush any buffered frames to the destination.</summary>
    ValueTask FlushAsync(CancellationToken ct);
}
