using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public class CodaBinaryResolverTests
{
    private static string Bundle(string b) => System.IO.Path.Combine(b, "coda", "coda.exe");

    [Fact]
    public void Host_AlwaysReturnsPathCoda()
    {
        var (path, fell) = CodaBinaryResolver.Resolve(CodaSource.Host, "/app", _ => true);
        Assert.Equal("coda", path);
        Assert.False(fell);
    }

    [Fact]
    public void Bundled_Present_ReturnsBundlePath()
    {
        var (path, fell) = CodaBinaryResolver.Resolve(CodaSource.Bundled, "/app", p => p == Bundle("/app"));
        Assert.Equal(Bundle("/app"), path);
        Assert.False(fell);
    }

    [Fact]
    public void Bundled_Absent_FallsBackToHost_WithFlag()
    {
        var (path, fell) = CodaBinaryResolver.Resolve(CodaSource.Bundled, "/app", _ => false);
        Assert.Equal("coda", path);
        Assert.True(fell);
    }

    [Fact]
    public void Auto_Present_UsesBundle_Absent_UsesHost()
    {
        Assert.Equal(Bundle("/app"), CodaBinaryResolver.Resolve(CodaSource.Auto, "/app", p => p == Bundle("/app")).Path);
        Assert.Equal("coda", CodaBinaryResolver.Resolve(CodaSource.Auto, "/app", _ => false).Path);
    }
}
