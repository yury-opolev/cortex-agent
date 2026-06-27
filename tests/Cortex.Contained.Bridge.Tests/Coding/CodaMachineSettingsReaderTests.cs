using Cortex.Contained.Bridge.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodaMachineSettingsReaderTests : IDisposable
{
    private readonly string tempHome = Path.Combine(Path.GetTempPath(), "cortex-coda-" + Guid.NewGuid().ToString("N"));

    private void Write(string json)
    {
        Directory.CreateDirectory(Path.Combine(this.tempHome, ".coda"));
        File.WriteAllText(Path.Combine(this.tempHome, ".coda", "settings.json"), json);
    }

    [Fact]
    public void Read_ReturnsDefaults()
    {
        this.Write("""{ "defaultProvider": "github-copilot", "defaultModel": "m1" }""");
        var (provider, model) = CodaMachineSettingsReader.Read(this.tempHome);
        Assert.Equal("github-copilot", provider);
        Assert.Equal("m1", model);
    }

    [Fact]
    public void Read_MissingOrGarbled_ReturnsNulls()
    {
        var (p1, m1) = CodaMachineSettingsReader.Read(this.tempHome);
        Assert.Null(p1);
        Assert.Null(m1);

        this.Write("{ broken");
        var (p2, m2) = CodaMachineSettingsReader.Read(this.tempHome);
        Assert.Null(p2);
        Assert.Null(m2);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempHome, recursive: true); } catch { }
    }
}
