using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Cortex.Contained.Contracts.SystemPrompt;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers the system-prompt runtime API (get/set/reset/preview) added to
/// <see cref="AgentRuntime"/>: round-tripping through a real file-backed
/// <see cref="SystemPromptStore"/> and, for preview, through the real
/// <see cref="PromptAssembler"/> so fidelity with the live model prompt is verified.
/// </summary>
public sealed class SystemPromptRuntimeTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "sp-runtime-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Builds an <see cref="AgentRuntime"/> the way <c>AgentRuntimeTests</c> does (minimal
    /// substitute graph), wired with a real <see cref="SystemPromptStore"/> over a temp
    /// directory so config get/set/reset/preview exercise real persistence.
    /// </summary>
    private AgentRuntime CreateRuntime(out SystemPromptStore store)
    {
        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        var mockLlmClient = Substitute.For<ILlmClient>();
        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([], activeChannelStore, NullLogger<ToolRegistry>.Instance);
        var messageChannel = new AgentMessageChannel();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);

        var mockCaller = Substitute.For<IAgentHubClient>();
        hubClients.Client(Arg.Any<string>()).Returns(mockCaller);
        bridgeAccessor.SetConnectionId("test-conn");

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());

        store = new SystemPromptStore(Path.Combine(this.dir, "system-prompt.json"), NullLogger<SystemPromptStore>.Instance);

        return new AgentRuntime(
            sessions,
            mockLlmClient,
            toolRegistry,
            sessionConfig,
            messageChannel,
            bridgeAccessor,
            activeChannelStore,
            httpClientFactory,
            Path.GetTempPath(),
            Path.GetTempPath(),
            NullLogger<AgentRuntime>.Instance,
            new ModelProvider(),
            imageAgingMonitor,
            systemPromptStore: store);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    [Fact]
    public async Task SetThenGet_RoundTripsConfig()
    {
        var runtime = CreateRuntime(out _);
        var config = SystemPromptDefaults.Create();
        config.CodingRelay = "relay-x";

        var result = await runtime.SetSystemPromptConfigAsync(config, CancellationToken.None);
        Assert.True(result.IsValid);

        var read = await runtime.GetSystemPromptConfigAsync(CancellationToken.None);
        Assert.Equal("relay-x", read.CodingRelay);
    }

    [Fact]
    public async Task Set_Invalid_ReturnsErrorsAndDoesNotPersist()
    {
        var runtime = CreateRuntime(out _);
        var bad = SystemPromptDefaults.Create();
        bad.SubagentTemplate = "{{unknown}}";

        var result = await runtime.SetSystemPromptConfigAsync(bad, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);

        var read = await runtime.GetSystemPromptConfigAsync(CancellationToken.None);
        Assert.Equal(SystemPromptDefaults.SubagentTemplate, read.SubagentTemplate);
    }

    [Fact]
    public async Task Reset_RestoresDefaultsAfterModification()
    {
        var runtime = CreateRuntime(out _);
        var config = SystemPromptDefaults.Create();
        config.CodingRelay = "relay-x";
        var setResult = await runtime.SetSystemPromptConfigAsync(config, CancellationToken.None);
        Assert.True(setResult.IsValid);

        var reset = await runtime.ResetSystemPromptConfigAsync(CancellationToken.None);

        Assert.Equal(SystemPromptDefaults.CodingRelay, reset.CodingRelay);
        var read = await runtime.GetSystemPromptConfigAsync(CancellationToken.None);
        Assert.Equal(SystemPromptDefaults.CodingRelay, read.CodingRelay);
    }

    [Fact]
    public async Task Preview_ReflectsStoredConfigThroughRealAssembler()
    {
        var runtime = CreateRuntime(out _);
        var config = SystemPromptDefaults.Create();
        config.CodingRelay = "PREVIEW_MARKER_XYZ";
        var setResult = await runtime.SetSystemPromptConfigAsync(config, CancellationToken.None);
        Assert.True(setResult.IsValid);

        var preview = await runtime.GetSystemPromptPreviewAsync("web", isVoice: false, CancellationToken.None);

        Assert.Contains("PREVIEW_MARKER_XYZ", preview, StringComparison.Ordinal);
    }
}
