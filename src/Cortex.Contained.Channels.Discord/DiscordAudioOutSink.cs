using Discord.Audio;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Production adapter that wraps a <see cref="Discord.Audio.AudioOutStream"/>.
/// Sets <see cref="IsAvailable"/> to false after disposal so the pipeline stops
/// trying to write.
/// </summary>
internal sealed class DiscordAudioOutSink : IAudioOutSink
{
    private readonly AudioOutStream stream;
    private volatile bool disposed;

    public DiscordAudioOutSink(AudioOutStream stream)
    {
        this.stream = stream;
    }

    public bool IsAvailable => !this.disposed;

    public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (this.disposed)
        {
            return ValueTask.CompletedTask;
        }
        return new ValueTask(this.stream.WriteAsync(frame, ct).AsTask());
    }

    public ValueTask FlushAsync(CancellationToken ct)
    {
        if (this.disposed)
        {
            return ValueTask.CompletedTask;
        }
        return new ValueTask(this.stream.FlushAsync(ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }
        this.disposed = true;
        await this.stream.DisposeAsync().ConfigureAwait(false);
    }
}
