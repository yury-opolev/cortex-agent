using Cortex.Contained.Bridge.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodaMcpSettingsTests
{
    // ── Store round-trip ───────────────────────────────────────────────────

    [Fact]
    public void Store_defaults_to_unset()
    {
        using var dir = new TempDir();
        var store = new CodaMcpSettingsStore(Path.Combine(dir.Path, "coda-mcp.json"));

        var settings = store.Get();

        Assert.Null(settings.Mcp); // unset → fall back to YAML
        Assert.Null(settings.CuratedMcpDir);
    }

    [Fact]
    public void Store_roundtrips_policy_and_curated_dir()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "coda-mcp.json");
        new CodaMcpSettingsStore(path).Set(CodaMcpPolicy.Curated, "  C:\\curated  ");

        var settings = new CodaMcpSettingsStore(path).Get();

        Assert.Equal(CodaMcpPolicy.Curated, settings.Mcp);
        Assert.Equal("C:\\curated", settings.CuratedMcpDir); // trimmed
    }

    [Fact]
    public void Store_blank_curated_dir_normalizes_to_null()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "coda-mcp.json");
        new CodaMcpSettingsStore(path).Set(CodaMcpPolicy.Host, "   ");

        Assert.Null(new CodaMcpSettingsStore(path).Get().CuratedMcpDir);
    }

    // ── Endpoint policy parsing ────────────────────────────────────────────

    [Theory]
    [InlineData(null, CodaMcpPolicy.Host)]
    [InlineData("", CodaMcpPolicy.Host)]
    [InlineData("host", CodaMcpPolicy.Host)]
    [InlineData("HOST", CodaMcpPolicy.Host)]
    [InlineData("curated", CodaMcpPolicy.Curated)]
    [InlineData("off", CodaMcpPolicy.Off)]
    public void ParsePolicy_accepts_known_values(string? value, CodaMcpPolicy expected)
    {
        var (ok, policy, error) = CodingMcpEndpoints.ParsePolicy(value);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(expected, policy);
    }

    [Fact]
    public void ParsePolicy_rejects_unknown()
    {
        var (ok, _, error) = CodingMcpEndpoints.ParsePolicy("bogus");

        Assert.False(ok);
        Assert.Contains("host, curated, off", error!);
    }

    [Fact]
    public void PolicyNames_are_host_curated_off()
    {
        Assert.Equal(["host", "curated", "off"], CodingMcpEndpoints.PolicyNames());
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coda-mcpui-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(this.Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(this.Path))
                {
                    Directory.Delete(this.Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
