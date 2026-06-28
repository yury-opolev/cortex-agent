using Cortex.Contained.Bridge.Mcp.Auth;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpStaticAuthTests
{
    private static McpStaticAuth Build(IMcpSecretResolver resolver)
        => new(resolver, NullLogger<McpStaticAuth>.Instance);

    [Fact]
    public void Resolve_StdioWithSecretRefEnv_ResolvesValueFromDpapi()
    {
        var resolver = Substitute.For<IMcpSecretResolver>();
        resolver.GetSecret("mcp/fs/token").Returns("s3cr3t");
        var server = new McpServerConfig
        {
            Key = "fs",
            Transport = McpTransport.Stdio,
            Env = new Dictionary<string, string>
            {
                ["TOKEN"] = "${secret:mcp/fs/token}",
                ["MODE"] = "prod",
            },
        };

        var resolved = Build(resolver).Resolve(server);

        Assert.Equal("s3cr3t", resolved.EnvironmentVariables["TOKEN"]);
        Assert.Equal("prod", resolved.EnvironmentVariables["MODE"]);
        Assert.False(resolved.NeedsAuth);
    }

    [Fact]
    public void Resolve_StdioMissingSecret_InjectsEmpty()
    {
        var resolver = Substitute.For<IMcpSecretResolver>();
        resolver.GetSecret(Arg.Any<string>()).Returns((string?)null);
        var server = new McpServerConfig
        {
            Key = "fs",
            Transport = McpTransport.Stdio,
            Env = new Dictionary<string, string> { ["TOKEN"] = "${secret:missing}" },
        };

        var resolved = Build(resolver).Resolve(server);

        Assert.Equal(string.Empty, resolved.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public void Resolve_HttpApiKeyDefaultHeader_AttachesBearer()
    {
        var resolver = Substitute.For<IMcpSecretResolver>();
        resolver.GetSecret("mcp/gh/apikey").Returns("pat123");
        var server = new McpServerConfig
        {
            Key = "gh",
            Transport = McpTransport.Http,
            Auth = McpAuthMode.ApiKey,
            SecretRef = "mcp/gh/apikey",
        };

        var resolved = Build(resolver).Resolve(server);

        Assert.Equal("Bearer pat123", resolved.Headers["Authorization"]);
        Assert.False(resolved.NeedsAuth);
    }

    [Fact]
    public void Resolve_HttpApiKeyCustomHeader_AttachesRawToken()
    {
        var resolver = Substitute.For<IMcpSecretResolver>();
        resolver.GetSecret("mcp/gh/apikey").Returns("pat123");
        var server = new McpServerConfig
        {
            Key = "gh",
            Transport = McpTransport.Http,
            Auth = McpAuthMode.ApiKey,
            ApiKeyHeader = "X-Api-Key",
            SecretRef = "mcp/gh/apikey",
        };

        var resolved = Build(resolver).Resolve(server);

        Assert.Equal("pat123", resolved.Headers["X-Api-Key"]);
    }

    [Fact]
    public void Resolve_HttpApiKeyMissingSecret_NeedsAuth()
    {
        var resolver = Substitute.For<IMcpSecretResolver>();
        resolver.GetSecret(Arg.Any<string>()).Returns((string?)null);
        var server = new McpServerConfig
        {
            Key = "gh",
            Transport = McpTransport.Http,
            Auth = McpAuthMode.ApiKey,
            SecretRef = "mcp/gh/apikey",
        };

        var resolved = Build(resolver).Resolve(server);

        Assert.True(resolved.NeedsAuth);
        Assert.Empty(resolved.Headers);
    }

    [Fact]
    public void Resolve_HttpNoneAndAuto_AttachNothing()
    {
        var resolver = Substitute.For<IMcpSecretResolver>();

        var none = Build(resolver).Resolve(new McpServerConfig { Key = "a", Transport = McpTransport.Http, Auth = McpAuthMode.None });
        var auto = Build(resolver).Resolve(new McpServerConfig { Key = "b", Transport = McpTransport.Http, Auth = McpAuthMode.Auto });

        Assert.Empty(none.Headers);
        Assert.False(none.NeedsAuth);
        Assert.Empty(auto.Headers);
        Assert.False(auto.NeedsAuth);
    }

    [Fact]
    public void Resolve_HttpOAuth_NeedsLoginPlaceholder()
    {
        var resolver = Substitute.For<IMcpSecretResolver>();

        var resolved = Build(resolver).Resolve(new McpServerConfig { Key = "gh", Transport = McpTransport.Http, Auth = McpAuthMode.OAuth });

        Assert.True(resolved.NeedsAuth);
        Assert.Contains("oauth", resolved.NeedsAuthReason!, StringComparison.OrdinalIgnoreCase);
    }
}
