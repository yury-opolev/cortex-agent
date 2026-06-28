using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpServerRequestMapperTests
{
    [Fact]
    public void ValidateNewKey_ValidUniqueKey_ReturnsNull()
    {
        var error = McpServerRequestMapper.ValidateNewKey("github", ["filesystem"]);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateNewKey_EmptyKey_ReturnsError()
    {
        var error = McpServerRequestMapper.ValidateNewKey("  ", []);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateNewKey_InvalidCharacters_ReturnsError()
    {
        var error = McpServerRequestMapper.ValidateNewKey("bad key", []);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateNewKey_DoubleUnderscore_ReturnsError()
    {
        var error = McpServerRequestMapper.ValidateNewKey("foo__bar", []);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateNewKey_DuplicateKeyCaseInsensitive_ReturnsError()
    {
        var error = McpServerRequestMapper.ValidateNewKey("GitHub", ["github"]);
        Assert.NotNull(error);
    }

    [Fact]
    public void FindDuplicateKey_NoDuplicates_ReturnsNull()
    {
        Assert.Null(McpServerRequestMapper.FindDuplicateKey(["github", "filesystem", "weather"]));
    }

    [Fact]
    public void FindDuplicateKey_CaseInsensitiveDuplicate_ReturnsTheKey()
    {
        // e.g. a hand-edited cortex.yml with two servers keyed "github" — the per-add validation
        // can't catch this, so the whole-list check must.
        var duplicate = McpServerRequestMapper.FindDuplicateKey(["github", "weather", "GitHub"]);

        Assert.Equal("github", duplicate);
    }

    [Fact]
    public void FullName_IsUniquePerDistinctKey_SoNoToolCollisionAcrossDistinctServers()
    {
        // Tool FullName = mcp__{serverKey}__{toolName}; server keys are unique and cannot contain
        // a '__' run, so two distinct valid keys can never produce the same FullName for a tool.
        var a = McpToolNamer.Full("alpha", "do_thing");
        var b = McpToolNamer.Full("beta", "do_thing");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToConfig_MapsAllEditableFields()
    {
        var request = new McpServerRequest
        {
            Key = "GitHub",
            Enabled = true,
            Transport = "http",
            Url = "https://api.example.com/mcp",
            Auth = "oauth",
            ApiKeyHeader = "X-Api-Key",
            ToolAllowList = ["search", "issues"],
        };

        var config = McpServerRequestMapper.ToConfig(request);

        Assert.Equal("github", config.Key);
        Assert.True(config.Enabled);
        Assert.Equal(McpTransport.Http, config.Transport);
        Assert.Equal("https://api.example.com/mcp", config.Url);
        Assert.Equal(McpAuthMode.OAuth, config.Auth);
        Assert.Equal("X-Api-Key", config.ApiKeyHeader);
        Assert.Equal(["search", "issues"], config.ToolAllowList);
    }

    [Fact]
    public void ToConfig_MapsStdioFields()
    {
        var request = new McpServerRequest
        {
            Key = "filesystem",
            Transport = "stdio",
            Command = "npx",
            Args = ["-y", "@modelcontextprotocol/server-filesystem", "C:\\data"],
            Env = new Dictionary<string, string> { ["ROOT"] = "C:\\data" },
            Auth = "none",
        };

        var config = McpServerRequestMapper.ToConfig(request);

        Assert.Equal(McpTransport.Stdio, config.Transport);
        Assert.Equal("npx", config.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "C:\\data"], config.Args);
        Assert.Equal("C:\\data", config.Env["ROOT"]);
        Assert.Equal(McpAuthMode.None, config.Auth);
    }

    [Fact]
    public void ToConfig_NullEnabled_DefaultsToTrue()
    {
        var config = McpServerRequestMapper.ToConfig(new McpServerRequest { Key = "x", Transport = "stdio", Command = "node" });
        Assert.True(config.Enabled);
    }

    [Fact]
    public void ApplyTo_NullFields_LeaveExistingUnchanged()
    {
        var existing = new McpServerConfig
        {
            Key = "github",
            Enabled = true,
            Transport = McpTransport.Http,
            Url = "https://example.com",
            Auth = McpAuthMode.OAuth,
            ToolAllowList = ["a"],
            SecretRef = "mcp/github/apikey",
        };

        McpServerRequestMapper.ApplyTo(existing, new McpServerRequest { Enabled = false });

        Assert.False(existing.Enabled);
        Assert.Equal(McpTransport.Http, existing.Transport);
        Assert.Equal("https://example.com", existing.Url);
        Assert.Equal(McpAuthMode.OAuth, existing.Auth);
        Assert.Equal(["a"], existing.ToolAllowList);
        // The mapper never touches secrets — SecretRef is preserved.
        Assert.Equal("mcp/github/apikey", existing.SecretRef);
    }

    [Fact]
    public void ApplyTo_AllowListOnly_UpdatesAllowListAndPreservesRest()
    {
        var existing = new McpServerConfig { Key = "github", Url = "https://example.com", Transport = McpTransport.Http };
        McpServerRequestMapper.ApplyTo(existing, new McpServerRequest { ToolAllowList = ["search"] });

        Assert.Equal(["search"], existing.ToolAllowList);
        Assert.Equal("https://example.com", existing.Url);
    }

    [Fact]
    public void NormalizeAllowList_TrimsDeduplicatesAndDropsEmpty()
    {
        var result = McpServerRequestMapper.NormalizeAllowList([" search ", "search", "", "  ", "issues"]);
        Assert.Equal(["search", "issues"], result);
    }

    [Fact]
    public void ApiKeySecretId_IsDeterministicAndNamespaced()
    {
        Assert.Equal("mcp/github/apikey", McpServerRequestMapper.ApiKeySecretId("GitHub"));
    }
}
