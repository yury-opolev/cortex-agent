using System.Collections.Concurrent;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Test fake for <see cref="IAudioOutSink"/>. Records every frame written.
/// Optional fault injection: <see cref="ThrowOnNextWrite"/> simulates Discord errors.
/// </summary>
internal sealed class RecordingAudioSink : IAudioOutSink
{
    private readonly ConcurrentQueue<byte[]> frames = new();
    private int flushCount;
    private bool isAvailable = true;

    public bool IsAvailable => this.isAvailable;
    public IReadOnlyCollection<byte[]> Frames => this.frames;
    public int FlushCount => Volatile.Read(ref this.flushCount);

    public Exception? ThrowOnNextWrite { get; set; }

    public void SetUnavailable() => this.isAvailable = false;

    public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (this.ThrowOnNextWrite is { } ex)
        {
            this.ThrowOnNextWrite = null;
            return ValueTask.FromException(ex);
        }
        this.frames.Enqueue(frame.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref this.flushCount);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        this.isAvailable = false;
        return ValueTask.CompletedTask;
    }
}
