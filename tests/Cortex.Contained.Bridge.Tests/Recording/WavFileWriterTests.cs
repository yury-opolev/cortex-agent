using System.Text;
using Cortex.Contained.Bridge.Recording;

namespace Cortex.Contained.Bridge.Tests.Recording;

public sealed class WavFileWriterTests : IDisposable
{
    private readonly string dir = Path.Combine(
        Path.GetTempPath(), "cortex-rec-" + Guid.NewGuid().ToString("N"));

    public WavFileWriterTests() => Directory.CreateDirectory(this.dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Empty_Finalise_HasMinimalHeaderAndZeroData()
    {
        var path = Path.Combine(this.dir, "a.wav");
        using (var w = WavFileWriter.Create(path, 16000))
        {
            w.Finalise();
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44, bytes.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(36, BitConverter.ToInt32(bytes, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(bytes, 16));
        Assert.Equal(1, BitConverter.ToInt16(bytes, 20));
        Assert.Equal(1, BitConverter.ToInt16(bytes, 22));
        Assert.Equal(16000, BitConverter.ToInt32(bytes, 24));
        Assert.Equal("data", Encoding.ASCII.GetString(bytes, 36, 4));
        Assert.Equal(0, BitConverter.ToInt32(bytes, 40));
    }

    [Fact]
    public void Append_ThenFinalise_RecordsCorrectDataSize()
    {
        var path = Path.Combine(this.dir, "b.wav");
        var pcm = new byte[3200]; // 100 ms at 16 kHz mono 16-bit
        using (var w = WavFileWriter.Create(path, 16000))
        {
            w.Append(pcm);
            w.Append(pcm);
            w.Finalise();
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44 + 6400, bytes.Length);
        Assert.Equal(36 + 6400, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(6400, BitConverter.ToInt32(bytes, 40));
    }

    [Fact]
    public void DisposeWithoutFinalise_StillFinalises()
    {
        var path = Path.Combine(this.dir, "c.wav");
        using (var w = WavFileWriter.Create(path, 16000))
        {
            w.Append(new byte[1600]);
            // Implicit Finalise via Dispose.
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(1600, BitConverter.ToInt32(bytes, 40));
    }

    [Fact]
    public void FinaliseFromFile_RepairsTornHeader()
    {
        var path = Path.Combine(this.dir, "torn.wav");

        // Create a WAV the manual way to simulate "header has size=0, data
        // chunk has real bytes appended" — i.e. a torn write that the
        // crash-recovery sweep must handle.
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            // 44-byte zero-sized PCM 16 kHz mono header
            var hdr = new byte[44];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(hdr, 0);
            BitConverter.GetBytes(36).CopyTo(hdr, 4);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(hdr, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(hdr, 12);
            BitConverter.GetBytes(16).CopyTo(hdr, 16);
            BitConverter.GetBytes((short)1).CopyTo(hdr, 20);
            BitConverter.GetBytes((short)1).CopyTo(hdr, 22);
            BitConverter.GetBytes(16000).CopyTo(hdr, 24);
            BitConverter.GetBytes(32000).CopyTo(hdr, 28);
            BitConverter.GetBytes((short)2).CopyTo(hdr, 32);
            BitConverter.GetBytes((short)16).CopyTo(hdr, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(hdr, 36);
            // hdr[40..44] left as zero (the torn size)
            fs.Write(hdr);
            fs.Write(new byte[1600]); // real PCM payload appended past the lie
        }

        WavFileWriter.FinaliseFromFile(path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(1600, BitConverter.ToInt32(bytes, 40));
        Assert.Equal(36 + 1600, BitConverter.ToInt32(bytes, 4));
    }
}
