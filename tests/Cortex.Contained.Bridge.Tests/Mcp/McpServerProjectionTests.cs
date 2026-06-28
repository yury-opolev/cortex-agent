using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpServerProjectionTests
{
    private static McpServerConfig Server(bool enabled = true, McpAuthMode auth = McpAuthMode.None, string? secretRef = null) => new()
    {
        Key = "github",
        Enabled = enabled,
        Transport = McpTransport.Http,
        Url = "https://example.com/mcp",
        Auth = auth,
        SecretRef = secretRef,
    };

    private static McpServerRuntimeInfo Runtime(McpServerStatus status, params string[] tools) => new()
    {
        Status = status,
        Tools = tools.Select(t => new McpToolDefinition
        {
            ServerKey = "github",
            ToolName = t,
            FullName = McpToolNamer.Full("github", t),
            Description = $"{t} desc",
            ParametersSchemaJson = "{}",
        }).ToList(),
    };

    [Fact]
    public void StatusLabel_MasterDisabled_ReturnsDisabled()
    {
        var label = McpServerProjection.StatusLabel(Server(), masterEnabled: false, runtime: Runtime(McpServerStatus.Connected), needsLogin: false);
        Assert.Equal("disabled", label);
    }

    [Fact]
    public void StatusLabel_ServerDisabled_ReturnsDisabled()
    {
        var label = McpServerProjection.StatusLabel(Server(enabled: false), masterEnabled: true, runtime: null, needsLogin: false);
        Assert.Equal("disabled", label);
    }

    [Fact]
    public void StatusLabel_Connected_ReturnsConnected()
    {
        var label = McpServerProjection.StatusLabel(Server(), masterEnabled: true, runtime: Runtime(McpServerStatus.Connected), needsLogin: false);
        Assert.Equal("connected", label);
    }

    [Fact]
    public void StatusLabel_Error_ReturnsError()
    {
        var label = McpServerProjection.StatusLabel(Server(), masterEnabled: true, runtime: Runtime(McpServerStatus.Error), needsLogin: false);
        Assert.Equal("error", label);
    }

    [Fact]
    public void StatusLabel_RuntimeNeedsLogin_ReturnsNeedsLogin()
    {
        var label = McpServerProjection.StatusLabel(Server(), masterEnabled: true, runtime: Runtime(McpServerStatus.NeedsLogin), needsLogin: false);
        Assert.Equal("needsLogin", label);
    }

    [Fact]
    public void StatusLabel_NoConnectionButNeedsLogin_ReturnsNeedsLogin()
    {
        var label = McpServerProjection.StatusLabel(Server(auth: McpAuthMode.OAuth), masterEnabled: true, runtime: null, needsLogin: true);
        Assert.Equal("needsLogin", label);
    }

    [Fact]
    public void StatusLabel_NoConnection_ReturnsDisconnected()
    {
        var label = McpServerProjection.StatusLabel(Server(), masterEnabled: true, runtime: null, needsLogin: false);
        Assert.Equal("disconnected", label);
    }

    [Fact]
    public void Project_RedactsSecret_ExposesHasSecretAndRefButNeverValue()
    {
        var view = McpServerProjection.Project(
            Server(auth: McpAuthMode.ApiKey, secretRef: "mcp/github/apikey"),
            masterEnabled: true,
            runtime: Runtime(McpServerStatus.Connected, "search"),
            needsLogin: false);

        Assert.True(view.HasSecret);
        Assert.Equal("mcp/github/apikey", view.SecretRef);
        Assert.Equal("apiKey", view.Auth);
        // The view has no property capable of carrying the secret value — only the reference id.
    }

    [Fact]
    public void Project_NoSecret_HasSecretFalse()
    {
        var view = McpServerProjection.Project(Server(), masterEnabled: true, runtime: null, needsLogin: false);
        Assert.False(view.HasSecret);
        Assert.Null(view.SecretRef);
    }

    [Fact]
    public void Project_CountsToolsAndNamesFromRuntime()
    {
        var view = McpServerProjection.Project(
            Server(),
            masterEnabled: true,
            runtime: Runtime(McpServerStatus.Connected, "search", "issues"),
            needsLogin: false);

        Assert.Equal(2, view.ToolCount);
        Assert.Equal(["search", "issues"], view.Tools);
        Assert.Equal("connected", view.Status);
        Assert.Equal("http", view.Transport);
    }

    [Fact]
    public void Project_RedactsPlaintextSecretEnv_KeepsRefAndPlainLiteral()
    {
        var config = new McpServerConfig
        {
            Key = "srv",
            Transport = McpTransport.Stdio,
            Command = "node",
            Env = new Dictionary<string, string>
            {
                ["RAW_TOKEN"] = "ghp_aB3dEfGh1jKlMn0pQrStUvWxYz1234567890", // high-entropy literal secret
                ["REF_TOKEN"] = "${secret:mcp/srv/token}",                  // a reference (no value)
                ["FLAG"] = "true",                                          // ordinary config
            },
        };

        var view = McpServerProjection.Project(config, masterEnabled: true, runtime: null, needsLogin: false);

        Assert.Equal("***redacted***", view.Env["RAW_TOKEN"]);
        Assert.Equal("${secret:mcp/srv/token}", view.Env["REF_TOKEN"]);
        Assert.Equal("true", view.Env["FLAG"]);
    }
}
