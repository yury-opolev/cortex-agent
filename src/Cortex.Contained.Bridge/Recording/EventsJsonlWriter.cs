using System.Text;

namespace Cortex.Contained.Bridge.Recording;

/// <summary>
/// Append-only JSONL writer. One JSON object per line, ending in <c>\n</c>.
/// Auto-flushes after every write so that a process kill leaves the file
/// parseable line-by-line (any partially-written tail is dropped at parse
/// time, but the preceding lines are intact).
/// </summary>
public sealed class EventsJsonlWriter : IDisposable
{
    private readonly StreamWriter writer;
    private readonly object writeLock = new();
    private bool disposed;

    private EventsJsonlWriter(StreamWriter writer)
    {
        this.writer = writer;
    }

    public static EventsJsonlWriter Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // FileShare.ReadWrite so external tools (and tests) can read or tail
        // the file while we're still actively writing to it. We're the only
        // writer; allowing the share doesn't introduce a second.
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var sw = new StreamWriter(fs, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        return new EventsJsonlWriter(sw);
    }

    public void WriteLine(string jsonLine)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        lock (this.writeLock)
        {
            this.writer.WriteLine(jsonLine);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        try
        {
            this.writer.Flush();
            this.writer.Dispose();
        }
        catch
        {
            // Best-effort: dispose path must not throw.
        }

        this.disposed = true;
    }
}
