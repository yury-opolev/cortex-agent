using Cortex.Contained.Bridge.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodaMcpEnvironmentTests
{
    [Fact]
    public void Curated_with_dir_sets_CODA_USER_MCP_DIR()
    {
        var env = CodaMcpEnvironment.Resolve(CodaMcpPolicy.Curated, "C:\\curated");

        Assert.Single(env);
        Assert.Equal("C:\\curated", env[CodaMcpEnvironment.UserMcpDirVar]);
    }

    [Theory]
    [InlineData(CodaMcpPolicy.Host)]
    [InlineData(CodaMcpPolicy.Off)]
    public void Non_curated_policies_set_no_env(CodaMcpPolicy policy)
    {
        // Even with a dir present, only Curated exports it.
        var env = CodaMcpEnvironment.Resolve(policy, "C:\\curated");

        Assert.Empty(env);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Curated_without_dir_sets_no_env(string? dir)
    {
        // Curated but no directory → degrade to host behavior (no env), never a half-set var.
        var env = CodaMcpEnvironment.Resolve(CodaMcpPolicy.Curated, dir);

        Assert.Empty(env);
    }

    [Fact]
    public void Curated_dir_is_trimmed()
    {
        var env = CodaMcpEnvironment.Resolve(CodaMcpPolicy.Curated, "  C:\\c  ");

        Assert.Equal("C:\\c", env[CodaMcpEnvironment.UserMcpDirVar]);
    }
}
