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
}
