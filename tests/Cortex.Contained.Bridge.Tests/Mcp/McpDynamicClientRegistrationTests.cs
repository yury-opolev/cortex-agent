using System.Text.Json;
using Cortex.Contained.Bridge.Mcp.Auth;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpDynamicClientRegistrationTests
{
    [Fact]
    public void BuildRequestJson_ProducesRfc7591Shape()
    {
        var json = McpDynamicClientRegistration.BuildRequestJson(
            clientName: "Cortex",
            redirectUri: "http://127.0.0.1:5080/mcp/oauth/callback",
            scope: "mcp:tools openid");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Cortex", root.GetProperty("client_name").GetString());
        Assert.Equal(
            "http://127.0.0.1:5080/mcp/oauth/callback",
            root.GetProperty("redirect_uris")[0].GetString());

        var grantTypes = root.GetProperty("grant_types").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("authorization_code", grantTypes);
        Assert.Contains("refresh_token", grantTypes);

        Assert.Equal("code", root.GetProperty("response_types")[0].GetString());
        Assert.Equal("none", root.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("mcp:tools openid", root.GetProperty("scope").GetString());
    }

    [Fact]
    public void BuildRequestJson_NoScope_OmitsScope()
    {
        var json = McpDynamicClientRegistration.BuildRequestJson("Cortex", "http://127.0.0.1:5080/cb", scope: null);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("scope", out _));
    }

    [Fact]
    public void ParseResponse_PublicClient_ParsesClientIdNoSecret()
    {
        const string json = """{ "client_id": "abc123", "client_id_issued_at": 1700000000 }""";

        var credentials = McpDynamicClientRegistration.ParseResponse(json);

        Assert.NotNull(credentials);
        Assert.Equal("abc123", credentials!.ClientId);
        Assert.Null(credentials.ClientSecret);
    }

    [Fact]
    public void ParseResponse_ConfidentialClient_ParsesClientIdAndSecret()
    {
        const string json = """{ "client_id": "abc123", "client_secret": "s3cr3t" }""";

        var credentials = McpDynamicClientRegistration.ParseResponse(json);

        Assert.Equal("abc123", credentials!.ClientId);
        Assert.Equal("s3cr3t", credentials.ClientSecret);
    }

    [Fact]
    public void ParseResponse_MissingClientId_ReturnsNull()
    {
        const string json = """{ "client_secret": "s3cr3t" }""";

        var credentials = McpDynamicClientRegistration.ParseResponse(json);

        Assert.Null(credentials);
    }
}
