namespace Cortex.Contained.Bridge.Recording;

/// <summary>
/// Streaming 16 kHz mono 16-bit PCM WAV writer. Writes a 44-byte RIFF header
/// on open (sizes = 0), appends raw PCM to the data chunk, rewrites the RIFF
/// and data sizes on <see cref="Finalise"/>. <see cref="FinaliseFromFile"/>
/// repairs torn artifacts where the process died before Finalise — the data
/// is on disk; only the two size fields need updating from the actual file
/// length.
/// </summary>
public sealed class WavFileWriter : IDisposable
{
    private const short ChannelsMono = 1;
    private const short BitsPerSample = 16;
    private const int HeaderSize = 44;

    private readonly FileStream stream;
    private long dataBytes;
    private bool finalised;
    private bool disposed;

    private WavFileWriter(FileStream stream, int sampleRate)
    {
        this.stream = stream;
        WriteHeader(stream, sampleRate, dataSize: 0);
    }

    public static WavFileWriter Create(string path, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        return new WavFileWriter(fs, sampleRate);
    }

    public void Append(ReadOnlySpan<byte> pcm16)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        this.stream.Write(pcm16);
        this.dataBytes += pcm16.Length;
    }

    public void Finalise()
    {
        if (this.finalised || this.disposed)
        {
            return;
        }

        this.stream.Flush();
        WriteSizes(this.stream, this.dataBytes);
        this.stream.Flush();
        this.finalised = true;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        try
        {
            if (!this.finalised)
            {
                this.Finalise();
            }
        }
        catch
        {
            // Swallow: dispose path must not throw. Crash recovery can still
            // repair the file from actual length.
        }

        this.stream.Dispose();
        this.disposed = true;
    }

    /// <summary>
    /// Open an existing WAV in place and rewrite its size fields from the
    /// actual file length minus the 44-byte header. Used by the startup crash
    /// recovery sweep on artifacts whose previous process died before
    /// <see cref="Finalise"/>.
    /// </summary>
    public static void FinaliseFromFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var data = fs.Length - HeaderSize;
        if (data < 0)
        {
            return;
        }

        WriteSizes(fs, data);
    }

    private static void WriteHeader(Stream s, int sampleRate, long dataSize)
    {
        var byteRate = sampleRate * ChannelsMono * BitsPerSample / 8;
        short blockAlign = ChannelsMono * BitsPerSample / 8;
        Span<byte> hdr = stackalloc byte[HeaderSize];

        WriteAscii(hdr, 0, "RIFF");
        BitConverter.TryWriteBytes(hdr.Slice(4, 4), (int)(36 + dataSize));
        WriteAscii(hdr, 8, "WAVE");
        WriteAscii(hdr, 12, "fmt ");
        BitConverter.TryWriteBytes(hdr.Slice(16, 4), 16);              // PCM fmt chunk size
        BitConverter.TryWriteBytes(hdr.Slice(20, 2), (short)1);         // audio format = PCM
        BitConverter.TryWriteBytes(hdr.Slice(22, 2), ChannelsMono);
        BitConverter.TryWriteBytes(hdr.Slice(24, 4), sampleRate);
        BitConverter.TryWriteBytes(hdr.Slice(28, 4), byteRate);
        BitConverter.TryWriteBytes(hdr.Slice(32, 2), blockAlign);
        BitConverter.TryWriteBytes(hdr.Slice(34, 2), BitsPerSample);
        WriteAscii(hdr, 36, "data");
        BitConverter.TryWriteBytes(hdr.Slice(40, 4), (int)dataSize);

        s.Position = 0;
        s.Write(hdr);
        s.Position = HeaderSize + dataSize;
    }

    private static void WriteSizes(Stream s, long dataBytes)
    {
        Span<byte> four = stackalloc byte[4];

        BitConverter.TryWriteBytes(four, (int)(36 + dataBytes));
        s.Position = 4;
        s.Write(four);

        BitConverter.TryWriteBytes(four, (int)dataBytes);
        s.Position = 40;
        s.Write(four);

        s.Position = HeaderSize + dataBytes;
    }

    private static void WriteAscii(Span<byte> dst, int at, string ascii)
    {
        for (var i = 0; i < ascii.Length; i++)
        {
            dst[at + i] = (byte)ascii[i];
        }
    }
}
