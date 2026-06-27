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

public class CompactionBehaviorTests : IAsyncLifetime
{
    private const string Summary = "## Goal\nTest summary.\n## Completed actions\nSent email to alice.";

    private readonly AgentSessionStore _sessions;
    private readonly AgentRuntime _runtime;
    private readonly ILlmClient _mockLlmClient;

    public CompactionBehaviorTests()
    {
        var sessionConfig = new SessionConfig();
        _sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        _mockLlmClient = Substitute.For<ILlmClient>();
        _mockLlmClient.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
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
        _runtime = new AgentRuntime(_sessions, _mockLlmClient, toolRegistry, sessionConfig, messageChannel, bridgeAccessor, activeChannelStore, httpClientFactory, Path.GetTempPath(), Path.GetTempPath(), NullLogger<AgentRuntime>.Instance, new ModelProvider(), imageAgingMonitor);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _runtime.StopProcessingAsync(CancellationToken.None);

    [Fact]
    public async Task CompactConversation_AtRestHistory_ProducesSingleSummaryUserMessage()
    {
        var session = _sessions.GetOrCreate("conv-at-rest");
        session.AddMessage(new LlmMessage { Role = "user", Content = "hi" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "hello" });
        session.AddMessage(new LlmMessage { Role = "user", Content = "how are you?" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "doing fine" });
        session.AddMessage(new LlmMessage { Role = "user", Content = "great" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "thanks" });

        var result = await _runtime.CompactChannelAsync("conv-at-rest", CancellationToken.None);

        Assert.True(result.Success);
        var history = session.GetHistory();
        Assert.Single(history);
        Assert.Equal("user", history[0].Role);
        Assert.Equal(LlmMessageType.CompactionSummary, history[0].MessageType);
        Assert.Contains(Summary, history[0].Content ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("continued from a previous conversation", history[0].Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactConversation_MidToolLoop_PreservesSummaryPlusLastToolRound()
    {
        var session = _sessions.GetOrCreate("conv-mid-tool");
        session.AddMessage(new LlmMessage { Role = "user", Content = "do the thing" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "ok" });
        session.AddMessage(new LlmMessage { Role = "user", Content = "go" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "searching" });
        session.AddMessage(new LlmMessage { Role = "user", Content = "and also search again" });
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls = [new LlmToolCall { Id = "call_9", Name = "search", Arguments = "{}" }],
        });
        session.AddMessage(new LlmMessage { Role = "tool", ToolCallId = "call_9", Content = "result-data" });

        var result = await _runtime.CompactChannelAsync("conv-mid-tool", CancellationToken.None);

        Assert.True(result.Success);
        var history = session.GetHistory();
        Assert.Equal(3, history.Count);

        Assert.Equal("user", history[0].Role);
        Assert.Equal(LlmMessageType.CompactionSummary, history[0].MessageType);
        Assert.Contains("tool call and its results follow", history[0].Content ?? string.Empty, StringComparison.Ordinal);

        Assert.Equal("assistant", history[1].Role);
        Assert.NotNull(history[1].ToolCalls);
        Assert.Equal("call_9", history[1].ToolCalls![0].Id);

        Assert.Equal("tool", history[2].Role);
        Assert.Equal("call_9", history[2].ToolCallId);
        Assert.Equal("result-data", history[2].Content);
    }

    [Fact]
    public async Task CompactConversation_Regression_DoesNotReInjectLastUserMessage()
    {
        // The original bug: a completed-action user message ("send email to X") was the
        // last user turn, agent acted, conversation went idle, then compaction re-injected
        // that same user message after the summary — causing the model to re-execute it.
        var session = _sessions.GetOrCreate("conv-replay");
        session.AddMessage(new LlmMessage { Role = "user", Content = "hello there" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "hi" });
        session.AddMessage(new LlmMessage { Role = "user", Content = "how was the meeting yesterday?" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "went well" });
        session.AddMessage(new LlmMessage { Role = "user", Content = "please send an email to alice about the meeting" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "email sent to alice" });

        var result = await _runtime.CompactChannelAsync("conv-replay", CancellationToken.None);

        Assert.True(result.Success);
        var history = session.GetHistory();

        // No standalone user message should carry the original email request verbatim.
        // The content only appears (if at all) inside the summary wrapper, not as
        // a separate pending turn the model would treat as unanswered.
        foreach (var msg in history)
        {
            if (msg.MessageType == LlmMessageType.CompactionSummary)
            {
                continue;
            }

            Assert.NotEqual("please send an email to alice about the meeting", msg.Content);
        }

        // History must not end with the re-injected "please send an email..." pattern
        // (the old bug shape was [user-summary, assistant-summary, user-reinjected]).
        Assert.DoesNotContain(history, m =>
            m.Role == "user"
            && m.MessageType != LlmMessageType.CompactionSummary
            && (m.Content ?? string.Empty).Contains("send an email", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompactConversation_TooFewMessages_SessionUntouched()
    {
        var session = _sessions.GetOrCreate("conv-small");
        session.AddMessage(new LlmMessage { Role = "user", Content = "hi" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "hello" });
        session.AddMessage(new LlmMessage { Role = "user", Content = "bye" });

        var result = await _runtime.CompactChannelAsync("conv-small", CancellationToken.None);

        Assert.False(result.Success);
        var history = session.GetHistory();
        Assert.Equal(3, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("hi", history[0].Content);

        // No LLM call should have been made (the early-return guard runs before the summarizer).
        await _mockLlmClient.DidNotReceive().CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());
    }
}
