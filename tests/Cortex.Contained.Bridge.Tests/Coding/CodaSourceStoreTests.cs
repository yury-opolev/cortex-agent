using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public class CodaSourceStoreTests : IDisposable
{
    private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coda-source-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Get_Unset_ReturnsNull()
    {
        Assert.Null(new CodaSourceStore(this.path).Get());
    }

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        new CodaSourceStore(this.path).Set(CodaSource.Host);
        Assert.Equal(CodaSource.Host, new CodaSourceStore(this.path).Get());
    }

    public void Dispose()
    {
        if (File.Exists(this.path)) { File.Delete(this.path); }
        GC.SuppressFinalize(this);
    }
}
