using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodaMcpEnvironmentTests
{
    // Locks the policy fan-out: the same policy must drive BOTH the serve args (--no-mcp) and the
    // process env (CODA_USER_MCP_DIR) consistently — Curated = env only, Off = flag only, Host = neither.
    [Theory]
    [InlineData(CodaMcpPolicy.Host, false, false)]
    [InlineData(CodaMcpPolicy.Curated, false, true)]
    [InlineData(CodaMcpPolicy.Off, true, false)]
    public void Policy_drives_args_and_env_consistently(CodaMcpPolicy policy, bool expectNoMcpFlag, bool expectEnv)
    {
        var args = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Prompt, isResume: false, mcp: policy);
        var env = CodaMcpEnvironment.Resolve(policy, "C:\\curated");

        Assert.Equal(expectNoMcpFlag, args.Contains("--no-mcp"));
        Assert.Equal(expectEnv, env.ContainsKey(CodaMcpEnvironment.UserMcpDirVar));
    }

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
