using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Bridge.Mcp.Auth;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpServerConnectionFactoryOAuthTests
{
    private static McpServerConnectionFactory Build(IMcpOAuthManager oauthManager)
    {
        var staticAuth = new McpStaticAuth(Substitute.For<IMcpSecretResolver>(), NullLogger<McpStaticAuth>.Instance);
        return new McpServerConnectionFactory(
            staticAuth,
            oauthManager,
            NullLoggerFactory.Instance,
            NullLogger<McpServerConnectionFactory>.Instance);
    }

    private static McpServerConfig OAuthServer()
        => new() { Key = "github", Transport = McpTransport.Http, Url = "https://mcp.example.com/mcp", Auth = McpAuthMode.OAuth };

    [Fact]
    public void TryCreate_OAuthWithoutTokens_ReturnsNullNeedsLogin()
    {
        var oauth = Substitute.For<IMcpOAuthManager>();
        oauth.HasTokens(Arg.Any<McpServerConfig>()).Returns(false);

        var connection = Build(oauth).TryCreate(OAuthServer());

        Assert.Null(connection);
    }

    [Fact]
    public void TryCreate_OAuthWithTokens_ReturnsHttpConnection()
    {
        var oauth = Substitute.For<IMcpOAuthManager>();
        oauth.HasTokens(Arg.Any<McpServerConfig>()).Returns(true);

        var connection = Build(oauth).TryCreate(OAuthServer());

        Assert.NotNull(connection);
        Assert.IsType<HttpMcpServerConnection>(connection);
        Assert.Equal("github", connection!.ServerKey);
    }

    [Fact]
    public void TryCreate_AutoWithOAuthTokens_ReturnsHttpConnection()
    {
        var oauth = Substitute.For<IMcpOAuthManager>();
        oauth.HasTokens(Arg.Any<McpServerConfig>()).Returns(true);
        var server = new McpServerConfig { Key = "srv", Transport = McpTransport.Http, Url = "https://mcp.example.com/mcp", Auth = McpAuthMode.Auto };

        var connection = Build(oauth).TryCreate(server);

        Assert.IsType<HttpMcpServerConnection>(connection);
    }

    [Fact]
    public void TryCreate_AutoWithoutOAuthTokens_FallsBackToPublic()
    {
        var oauth = Substitute.For<IMcpOAuthManager>();
        oauth.HasTokens(Arg.Any<McpServerConfig>()).Returns(false);
        var server = new McpServerConfig { Key = "srv", Transport = McpTransport.Http, Url = "https://mcp.example.com/mcp", Auth = McpAuthMode.Auto };

        var connection = Build(oauth).TryCreate(server);

        // Auto with no OAuth tokens connects unauthenticated (public) — not skipped.
        Assert.IsType<HttpMcpServerConnection>(connection);
    }
}
