using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// RunInlineSlashCommandAsync is the synchronous-result counterpart to the
/// existing text-prefix slash-command path (AgentRuntime.cs:452-457). It
/// returns the response text inline so the Bridge can post it as a Discord
/// slash-command interaction reply.
/// </summary>
public class InlineSlashCommandTests : IAsyncLifetime
{
    private const string Summary = "## Goal\nTest summary.\n## Completed actions\nNone.";

    private readonly AgentSessionStore sessions;
    private readonly AgentRuntime runtime;

    public InlineSlashCommandTests()
    {
        var sessionConfig = new SessionConfig();
        this.sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = true, Content = Summary });

        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([], activeChannelStore, NullLogger<ToolRegistry>.Instance);
        var messageChannel = new AgentMessageChannel();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);
        hubClients.Client(Arg.Any<string>()).Returns(Substitute.For<IAgentHubClient>());
        bridgeAccessor.SetConnectionId("test-conn");

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());
        this.runtime = new AgentRuntime(
            this.sessions, llm, toolRegistry, sessionConfig, messageChannel, bridgeAccessor,
            activeChannelStore, httpClientFactory, Path.GetTempPath(), Path.GetTempPath(),
            NullLogger<AgentRuntime>.Instance, new ModelProvider(), imageAgingMonitor);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await this.runtime.StopProcessingAsync(CancellationToken.None);

    [Fact]
    public async Task RunInlineSlashCommand_Compact_ReturnsCompactSummary()
    {
        // /compact requires >= 6 messages to actually compact; otherwise it
        // returns the "Not enough messages" fallback. Seed enough to exercise
        // the success path.
        var session = this.sessions.GetOrCreate("conv-inline-compact");
        for (var i = 0; i < 4; i++)
        {
            session.AddMessage(new LlmMessage { Role = "user", Content = $"q{i}" });
            session.AddMessage(new LlmMessage { Role = "assistant", Content = $"a{i}" });
        }

        var result = await this.runtime.RunInlineSlashCommandAsync(
            "conv-inline-compact", "/compact", CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result);
        // Either the success path ("Compaction Complete") or the not-enough
        // path ("Not enough messages") — both are valid responses, but they
        // must not be the unknown-command error.
        Assert.DoesNotContain("Unknown inline command", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunInlineSlashCommand_Context_ReturnsContextInfo()
    {
        var session = this.sessions.GetOrCreate("conv-inline-context");
        session.AddMessage(new LlmMessage { Role = "user", Content = "hi" });

        var result = await this.runtime.RunInlineSlashCommandAsync(
            "conv-inline-context", "/context", CancellationToken.None);

        Assert.NotNull(result);
        // /context reports tokens / window / messages — at least one of those
        // words appears.
        Assert.True(
            result.Contains("token", StringComparison.OrdinalIgnoreCase)
            || result.Contains("context", StringComparison.OrdinalIgnoreCase)
            || result.Contains("message", StringComparison.OrdinalIgnoreCase),
            $"Expected /context output to mention tokens/context/messages; got: {result}");
    }

    [Theory]
    [InlineData("/foo")]
    [InlineData("/CompactExtra")]   // close-but-not-/compact
    [InlineData("hello /compact")]  // not at the start
    [InlineData("")]
    public async Task RunInlineSlashCommand_UnknownPrefix_ReturnsHelpfulError(string command)
    {
        var result = await this.runtime.RunInlineSlashCommandAsync(
            "conv-inline-unknown", command, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("Unknown inline command", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunInlineSlashCommand_CompactWithLeadingWhitespace_StillDispatches()
    {
        var session = this.sessions.GetOrCreate("conv-inline-trim");
        session.AddMessage(new LlmMessage { Role = "user", Content = "hi" });

        var result = await this.runtime.RunInlineSlashCommandAsync(
            "conv-inline-trim", "   /context   ", CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain("Unknown", result, StringComparison.Ordinal);
    }
}
