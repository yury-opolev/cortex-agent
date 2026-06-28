using Cortex.Contained.Bridge.Mcp.Auth;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public class McpUrlSecurityTests
{
    [Theory]
    [InlineData("http://example.com/mcp", true)]      // remote plaintext -> insecure
    [InlineData("http://10.0.0.5:8080/mcp", true)]     // remote LAN plaintext -> insecure
    [InlineData("https://example.com/mcp", false)]     // tls -> ok
    [InlineData("http://localhost:8080/mcp", false)]   // loopback http -> ok (local server)
    [InlineData("http://127.0.0.1/mcp", false)]        // loopback http -> ok
    [InlineData("not-a-url", false)]                    // unparseable -> not flagged here
    [InlineData(null, false)]
    public void IsInsecureForCredentials_Cases(string? url, bool expected)
    {
        Assert.Equal(expected, McpUrlSecurity.IsInsecureForCredentials(url));
    }

    [Theory]
    [InlineData("https://auth.example.com/token", true)]   // https anywhere -> allowed
    [InlineData("https://10.0.0.5/token", true)]            // https on a private host -> allowed
    [InlineData("http://localhost:9000/token", true)]      // loopback http (local AS) -> allowed
    [InlineData("http://127.0.0.1/token", true)]
    [InlineData("http://auth.example.com/token", false)]   // plaintext remote -> rejected (exfil)
    [InlineData("http://169.254.169.254/latest", false)]   // cloud metadata SSRF -> rejected
    [InlineData("http://10.0.0.5:8080/token", false)]      // internal plaintext -> rejected (SSRF)
    [InlineData("ftp://example.com/x", false)]             // wrong scheme -> rejected
    [InlineData("not-a-url", false)]
    [InlineData(null, false)]
    public void IsAllowedOAuthEndpoint_Cases(string? url, bool expected)
    {
        Assert.Equal(expected, McpUrlSecurity.IsAllowedOAuthEndpoint(url));
    }
}
