using Cortex.Contained.Bridge.Mcp.Auth;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpOAuthMetadataTests
{
    [Theory]
    [InlineData(
        "Bearer resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\"",
        "https://mcp.example.com/.well-known/oauth-protected-resource")]
    [InlineData(
        "Bearer realm=\"mcp\", error=\"invalid_token\", resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\"",
        "https://mcp.example.com/.well-known/oauth-protected-resource")]
    [InlineData(
        "Bearer resource_metadata=https://mcp.example.com/meta",
        "https://mcp.example.com/meta")]
    public void ParseResourceMetadataUrl_WithParam_ExtractsUrl(string header, string expected)
    {
        var url = McpOAuthMetadata.ParseResourceMetadataUrl(header);

        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer realm=\"mcp\"")]
    [InlineData("Basic")]
    public void ParseResourceMetadataUrl_NoParam_ReturnsNull(string? header)
    {
        var url = McpOAuthMetadata.ParseResourceMetadataUrl(header);

        Assert.Null(url);
    }

    [Fact]
    public void ParseAuthorizationServers_WithServers_ReturnsAll()
    {
        const string json = """
        {
          "resource": "https://mcp.example.com",
          "authorization_servers": ["https://auth.example.com", "https://auth2.example.com"]
        }
        """;

        var servers = McpOAuthMetadata.ParseAuthorizationServers(json);

        Assert.Equal(2, servers.Count);
        Assert.Equal("https://auth.example.com", servers[0]);
        Assert.Equal("https://auth2.example.com", servers[1]);
    }

    [Fact]
    public void ParseAuthorizationServers_Missing_ReturnsEmpty()
    {
        const string json = """{ "resource": "https://mcp.example.com" }""";

        var servers = McpOAuthMetadata.ParseAuthorizationServers(json);

        Assert.Empty(servers);
    }

    [Fact]
    public void ParseAuthorizationServerMetadata_FullDoc_ParsesEndpoints()
    {
        const string json = """
        {
          "issuer": "https://auth.example.com",
          "authorization_endpoint": "https://auth.example.com/authorize",
          "token_endpoint": "https://auth.example.com/token",
          "registration_endpoint": "https://auth.example.com/register",
          "scopes_supported": ["openid", "mcp:tools"]
        }
        """;

        var endpoints = McpOAuthMetadata.ParseAuthorizationServerMetadata(json);

        Assert.NotNull(endpoints);
        Assert.Equal("https://auth.example.com/authorize", endpoints!.AuthorizationEndpoint);
        Assert.Equal("https://auth.example.com/token", endpoints.TokenEndpoint);
        Assert.Equal("https://auth.example.com/register", endpoints.RegistrationEndpoint);
        Assert.Equal(["openid", "mcp:tools"], endpoints.ScopesSupported);
    }

    [Fact]
    public void ParseAuthorizationServerMetadata_OidcConfigWithoutRegistration_ParsesAndNullRegistration()
    {
        const string json = """
        {
          "authorization_endpoint": "https://auth.example.com/authorize",
          "token_endpoint": "https://auth.example.com/token"
        }
        """;

        var endpoints = McpOAuthMetadata.ParseAuthorizationServerMetadata(json);

        Assert.NotNull(endpoints);
        Assert.Null(endpoints!.RegistrationEndpoint);
        Assert.Empty(endpoints.ScopesSupported);
    }

    [Fact]
    public void ParseAuthorizationServerMetadata_MissingRequiredEndpoints_ReturnsNull()
    {
        const string json = """{ "issuer": "https://auth.example.com" }""";

        var endpoints = McpOAuthMetadata.ParseAuthorizationServerMetadata(json);

        Assert.Null(endpoints);
    }

    [Fact]
    public void ParseAuthorizationServerMetadata_NonHttpsAuthorizationEndpoint_ReturnsNull()
    {
        // SECURITY: a server-controlled file:// (or other non-web) authorization endpoint would be
        // shell-opened on the host — it must be rejected at parse time.
        const string json = """{"authorization_endpoint":"file://attacker/share","token_endpoint":"https://auth.example.com/token"}""";

        Assert.Null(McpOAuthMetadata.ParseAuthorizationServerMetadata(json));
    }

    [Fact]
    public void ParseAuthorizationServerMetadata_PlaintextHttpTokenEndpoint_ReturnsNull()
    {
        const string json = """{"authorization_endpoint":"https://auth.example.com/authorize","token_endpoint":"http://auth.example.com/token"}""";

        Assert.Null(McpOAuthMetadata.ParseAuthorizationServerMetadata(json));
    }

    [Fact]
    public void ParseAuthorizationServerMetadata_HttpsEndpoints_Parses()
    {
        const string json = """{"authorization_endpoint":"https://auth.example.com/authorize","token_endpoint":"https://auth.example.com/token"}""";

        var endpoints = McpOAuthMetadata.ParseAuthorizationServerMetadata(json);

        Assert.NotNull(endpoints);
        Assert.Equal("https://auth.example.com/authorize", endpoints!.AuthorizationEndpoint);
    }
}
