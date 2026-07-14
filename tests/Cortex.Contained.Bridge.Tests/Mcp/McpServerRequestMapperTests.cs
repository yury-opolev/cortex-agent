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
    public void ToConfig_MapsMutationToolAllowList_NormalizedLikeToolAllowList()
    {
        var request = new McpServerRequest
        {
            Key = "github",
            Transport = "http",
            Url = "https://api.example.com/mcp",
            ToolAllowList = ["search", "create_issue"],
            MutationToolAllowList = [" create_issue ", "create_issue", "", "  "],
        };

        var config = McpServerRequestMapper.ToConfig(request);

        Assert.Equal(["create_issue"], config.MutationToolAllowList);
    }

    [Fact]
    public void ApplyTo_MutationAllowListOnly_UpdatesMutationListAndPreservesRest()
    {
        var existing = new McpServerConfig
        {
            Key = "github",
            Url = "https://example.com",
            Transport = McpTransport.Http,
            ToolAllowList = ["search", "create_issue"],
        };

        McpServerRequestMapper.ApplyTo(existing, new McpServerRequest { MutationToolAllowList = ["create_issue"] });

        Assert.Equal(["create_issue"], existing.MutationToolAllowList);
        Assert.Equal(["search", "create_issue"], existing.ToolAllowList);
        Assert.Equal("https://example.com", existing.Url);
    }

    [Fact]
    public void ApplyTo_NullMutationAllowList_LeavesExistingUnchanged()
    {
        var existing = new McpServerConfig { Key = "github", MutationToolAllowList = ["create_issue"] };

        McpServerRequestMapper.ApplyTo(existing, new McpServerRequest { Enabled = false });

        Assert.Equal(["create_issue"], existing.MutationToolAllowList);
    }

    [Fact]
    public void ValidateMutationAllowList_EmptyToolAllowList_AllowsAnyMutationList()
    {
        // An empty exposure allow-list means "all tools exposed" — every mutation tool is
        // trivially exposed, so the consistency rule holds.
        Assert.Null(McpServerRequestMapper.ValidateMutationAllowList([], ["create_issue"]));
    }

    [Fact]
    public void ValidateMutationAllowList_MutationSubsetOfAllowList_ReturnsNull()
    {
        Assert.Null(McpServerRequestMapper.ValidateMutationAllowList(
            ["search", "create_issue"], ["create_issue"]));
    }

    [Fact]
    public void ValidateMutationAllowList_MutationToolMissingFromRestrictedAllowList_ReturnsError()
    {
        var error = McpServerRequestMapper.ValidateMutationAllowList(["search"], ["create_issue"]);

        Assert.NotNull(error);
        Assert.Contains("create_issue", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateMutationPolicy_PartialEditRequest_ValidatesEffectiveListsWithoutMutatingTarget()
    {
        // A partial PUT that shrinks the exposure allow-list below an existing mutation tool
        // must be rejected — and the target config must be left untouched by the validation.
        var existing = new McpServerConfig
        {
            Key = "github",
            ToolAllowList = ["search", "create_issue"],
            MutationToolAllowList = ["create_issue"],
        };

        var error = McpServerRequestMapper.ValidateMutationPolicy(
            existing, new McpServerRequest { ToolAllowList = ["search"] });

        Assert.NotNull(error);
        Assert.Equal(["search", "create_issue"], existing.ToolAllowList);
        Assert.Equal(["create_issue"], existing.MutationToolAllowList);
    }

    [Fact]
    public void ValidateMutationPolicy_ConsistentEdit_ReturnsNull()
    {
        var existing = new McpServerConfig { Key = "github", ToolAllowList = ["search"] };

        var error = McpServerRequestMapper.ValidateMutationPolicy(
            existing,
            new McpServerRequest { ToolAllowList = ["search", "create_issue"], MutationToolAllowList = ["create_issue"] });

        Assert.Null(error);
    }

    [Fact]
    public void ApiKeySecretId_IsDeterministicAndNamespaced()
    {
        Assert.Equal("mcp/github/apikey", McpServerRequestMapper.ApiKeySecretId("GitHub"));
    }

    [Fact]
    public void ValidateBounds_NullFields_ReturnsNull()
    {
        // A partial edit that doesn't touch either bound must not be rejected.
        Assert.Null(McpServerRequestMapper.ValidateBounds(new McpServerRequest()));
    }

    [Fact]
    public void ValidateBounds_ValidValues_ReturnsNull()
    {
        var error = McpServerRequestMapper.ValidateBounds(
            new McpServerRequest { CallTimeoutSeconds = 30, MaxResultBytes = 4096 });

        Assert.Null(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(60)] // must stay strictly BELOW the Agent gateway's 60s ceiling
    [InlineData(1000)]
    public void ValidateBounds_InvalidCallTimeoutSeconds_Rejected(int value)
    {
        // Out-of-range values are REJECTED, never silently clamped.
        var error = McpServerRequestMapper.ValidateBounds(new McpServerRequest { CallTimeoutSeconds = value });

        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateBounds_InvalidMaxResultBytes_Rejected(int value)
    {
        var error = McpServerRequestMapper.ValidateBounds(new McpServerRequest { MaxResultBytes = value });

        Assert.NotNull(error);
    }

    [Fact]
    public void ApplyTo_CallTimeoutSecondsAndMaxResultBytes_UpdatesTargetWhenProvided()
    {
        var existing = new McpServerConfig { Key = "github", CallTimeoutSeconds = 45, MaxResultBytes = 50 * 1024 };

        McpServerRequestMapper.ApplyTo(existing, new McpServerRequest { CallTimeoutSeconds = 20, MaxResultBytes = 2048 });

        Assert.Equal(20, existing.CallTimeoutSeconds);
        Assert.Equal(2048, existing.MaxResultBytes);
    }

    [Fact]
    public void ApplyTo_NullBoundsFields_LeavesExistingBoundsUnchanged()
    {
        var existing = new McpServerConfig { Key = "github", CallTimeoutSeconds = 30, MaxResultBytes = 4096 };

        McpServerRequestMapper.ApplyTo(existing, new McpServerRequest { Enabled = false });

        Assert.Equal(30, existing.CallTimeoutSeconds);
        Assert.Equal(4096, existing.MaxResultBytes);
    }
}
