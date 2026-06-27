using System.Text;
using System.Text.Json;
using Cortex.Contained.Bridge.Recording;

namespace Cortex.Contained.Bridge.Tests.Recording;

public sealed class EventsJsonlWriterTests : IDisposable
{
    private readonly string dir = Path.Combine(
        Path.GetTempPath(), "cortex-rec-" + Guid.NewGuid().ToString("N"));

    public EventsJsonlWriterTests() => Directory.CreateDirectory(this.dir);

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
    public void AppendsOnePerLine_NoArrayWrapping()
    {
        var path = Path.Combine(this.dir, "events.jsonl");
        using (var w = EventsJsonlWriter.Open(path))
        {
            w.WriteLine("{\"t\":0,\"type\":\"a\"}");
            w.WriteLine("{\"t\":1,\"type\":\"b\"}");
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal("a", JsonDocument.Parse(lines[0]).RootElement.GetProperty("type").GetString());
        Assert.Equal("b", JsonDocument.Parse(lines[1]).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void EachWrite_IsFlushedToDisk()
    {
        // AutoFlush after WriteLine means a process kill at this exact moment
        // would leave the line we just wrote on disk and parseable. Open the
        // reader with FileShare.ReadWrite to coexist with our still-open writer.
        var path = Path.Combine(this.dir, "events.jsonl");
        var w = EventsJsonlWriter.Open(path);
        try
        {
            w.WriteLine("{\"t\":0,\"type\":\"a\"}");

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            var bytes = ms.ToArray();

            Assert.NotEmpty(bytes);
            Assert.EndsWith("\n", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            w.Dispose();
        }
    }

    [Fact]
    public void Reopen_Appends_DoesNotOverwrite()
    {
        var path = Path.Combine(this.dir, "events.jsonl");
        using (var a = EventsJsonlWriter.Open(path))
        {
            a.WriteLine("{\"t\":0}");
        }

        using (var b = EventsJsonlWriter.Open(path))
        {
            b.WriteLine("{\"t\":1}");
        }

        Assert.Equal(2, File.ReadAllLines(path).Length);
    }
}
