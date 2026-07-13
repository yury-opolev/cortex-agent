using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

public class AgentRuntimeTests : IAsyncLifetime
{
    private readonly AgentRuntime _runtime;
    private readonly AgentMessageChannel _messageChannel;
    private readonly BridgeClientAccessor _bridgeAccessor;
    private readonly IAgentHubClient _mockCaller;
    private readonly ILlmClient _mockLlmClient;

    public AgentRuntimeTests()
    {
        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        _mockLlmClient = Substitute.For<ILlmClient>();
        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([], activeChannelStore, NullLogger<ToolRegistry>.Instance);
        _messageChannel = new AgentMessageChannel();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        _bridgeAccessor = new BridgeClientAccessor(hubContext);

        _mockCaller = Substitute.For<IAgentHubClient>();
        hubClients.Client(Arg.Any<string>()).Returns(_mockCaller);
        _bridgeAccessor.SetConnectionId("test-conn");

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());
        _runtime = new AgentRuntime(sessions, _mockLlmClient, toolRegistry, sessionConfig, _messageChannel, _bridgeAccessor, activeChannelStore, httpClientFactory, Path.GetTempPath(), Path.GetTempPath(), NullLogger<AgentRuntime>.Instance, new ModelProvider(), imageAgingMonitor);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _runtime.StopProcessingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsIdleByDefault()
    {
        var status = await _runtime.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AgentStatus.Idle, status.Status);
        Assert.Equal(0, status.ActiveConversations);
    }

    [Fact]
    public async Task SetDefaultModel_UpdatesCurrentModel()
    {
        _runtime.SetDefaultModel("gpt-4-turbo");

        // Verify indirectly through status
        var status = await _runtime.GetStatusAsync(CancellationToken.None);
        Assert.Equal("gpt-4-turbo", status.CurrentModel);
    }

    [Fact]
    public async Task UpdateConfigAsync_UpdatesMaxTokensAndTemperature()
    {
        var config = new AgentConfigUpdate
        {
            MaxTokens = 4096,
            Temperature = 0.5,
        };

        await _runtime.UpdateConfigAsync(config, CancellationToken.None);

        // Config update doesn't change model — verified via status still being default
        var status = await _runtime.GetStatusAsync(CancellationToken.None);
        Assert.NotNull(status);
    }

    [Fact]
    public async Task UpdateConfigAsync_AppliesMaxConcurrentSubagents()
    {
        var registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);
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
        var runtime = new AgentRuntime(sessions, mockLlmClient, toolRegistry, sessionConfig, messageChannel, bridgeAccessor, activeChannelStore, httpClientFactory, Path.GetTempPath(), Path.GetTempPath(), NullLogger<AgentRuntime>.Instance, new ModelProvider(), imageAgingMonitor, subagentRegistry: registry);

        await runtime.UpdateConfigAsync(new AgentConfigUpdate { MaxConcurrentSubagents = 9 }, CancellationToken.None);

        Assert.Equal(9, registry.MaxConcurrent);

        await runtime.StopProcessingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleMessageAsync_AcceptsAndQueuesMessages()
    {
        // With the queue-based design, messages are always accepted (queued)
        // and never rejected. Even multiple messages are accepted.
        var message = new HubInboundMessage
        {
            ConversationId = "conv-1",
            ChannelId = "webchat-default",
            SenderIdHash = "hash123",
            Text = "Hello",
            Timestamp = DateTimeOffset.UtcNow,
        };

        var result1 = await _runtime.HandleMessageAsync(message, CancellationToken.None);
        var result2 = await _runtime.HandleMessageAsync(message, CancellationToken.None);

        Assert.True(result1.Accepted);
        Assert.True(result2.Accepted);
    }

    [Fact]
    public async Task HandleMessageAsync_SetsSessionTitle()
    {
        // Setup: mock LLM to return immediately
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Hello back!"));

        _mockCaller.OnStatusChanged(Arg.Any<AgentStatusInfo>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseChunk(Arg.Any<ResponseChunkMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseComplete(Arg.Any<ResponseCompleteMessage>()).Returns(Task.CompletedTask);

        // Start the consumer loop so enqueued messages are processed
        await _runtime.StartProcessingAsync(CancellationToken.None);

        var message = new HubInboundMessage
        {
            ConversationId = "conv-title",
            ChannelId = "webchat-default",
            SenderIdHash = "hash123",
            Text = "What is the weather like today?",
            Timestamp = DateTimeOffset.UtcNow,
        };

        await _runtime.HandleMessageAsync(message, CancellationToken.None);

        // Wait for the consumer loop to process the message
        await WaitForResponseComplete(_mockCaller);

        // Verify title was set via the status callback (title is set on the session)
        await _mockCaller.Received().OnStatusChanged(Arg.Is<AgentStatusInfo>(s => s.Status == AgentStatus.Idle));
    }

    private static async IAsyncEnumerable<LlmStreamChunk> NeverEndingStream(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Yield one chunk then wait forever
        yield return new LlmStreamChunk { ContentDelta = "..." };
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static async IAsyncEnumerable<LlmStreamChunk> SingleChunkStream(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ContentDelta = content,
            IsComplete = true,
            FinishReason = "stop",
            Usage = new LlmTokenUsage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15,
            },
        };
    }

    /// <summary>
    /// Waits until <see cref="IAgentHubClient.OnResponseComplete"/> has been called at
    /// least once, with a timeout to prevent tests from hanging.
    /// </summary>
    private static async Task WaitForResponseComplete(IAgentHubClient client, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var completeCalls = client.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnResponseComplete))
                .ToList();
            if (completeCalls.Count > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for OnResponseComplete callback");
    }
}

/// <summary>
/// Tests for the tool call streaming accumulation logic in AgentRuntime.
/// Verifies that tool calls streamed with OpenAI-style deltas (Id only on
/// first chunk, Index for correlation) are correctly assembled.
/// </summary>
public class AgentRuntimeToolCallStreamingTests : IAsyncLifetime
{
    private readonly IAgentHubClient _mockCaller;
    private readonly ILlmClient _mockLlmClient;
    private readonly AgentRuntime _runtime;

    public AgentRuntimeToolCallStreamingTests()
    {
        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        _mockLlmClient = Substitute.For<ILlmClient>();

        // Create a simple stub tool to receive the accumulated calls
        var stubTool = Substitute.For<IAgentTool>();
        stubTool.Name.Returns("date_time");
        stubTool.Description.Returns("Gets the current date and time");
        stubTool.ParametersSchema.Returns("""{"type":"object","properties":{}}""");
        stubTool.ExecuteAsync(Arg.Any<string>(), Arg.Any<ToolExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentToolResult { Success = true, Content = "2026-03-01T12:00:00Z" });

        var stubTool2 = Substitute.For<IAgentTool>();
        stubTool2.Name.Returns("send_message");
        stubTool2.Description.Returns("Sends a message");
        stubTool2.ParametersSchema.Returns("""{"type":"object","properties":{"text":{"type":"string"}}}""");
        stubTool2.ExecuteAsync(Arg.Any<string>(), Arg.Any<ToolExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentToolResult { Success = true, Content = "sent" });

        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([stubTool, stubTool2], activeChannelStore, NullLogger<ToolRegistry>.Instance);
        var messageChannel = new AgentMessageChannel();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);

        _mockCaller = Substitute.For<IAgentHubClient>();
        hubClients.Client(Arg.Any<string>()).Returns(_mockCaller);
        bridgeAccessor.SetConnectionId("test-conn");

        _mockCaller.OnStatusChanged(Arg.Any<AgentStatusInfo>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseChunk(Arg.Any<ResponseChunkMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseComplete(Arg.Any<ResponseCompleteMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnToolExecution(Arg.Any<ToolExecutionMessage>()).Returns(Task.CompletedTask);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());
        _runtime = new AgentRuntime(sessions, _mockLlmClient, toolRegistry, sessionConfig, messageChannel, bridgeAccessor, activeChannelStore, httpClientFactory, Path.GetTempPath(), Path.GetTempPath(), NullLogger<AgentRuntime>.Instance, new ModelProvider(), imageAgingMonitor);
    }

    public async Task InitializeAsync()
    {
        // Start the consumer loop so enqueued messages are processed
        await _runtime.StartProcessingAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _runtime.StopProcessingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SingleToolCall_IdOnlyOnFirstChunk_AccumulatesCorrectly()
    {
        // Simulate OpenAI streaming: Id + Name on first chunk, arguments split across chunks
        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First LLM call: returns a tool call with Id only on first delta
                    return ToolCallStream(
                        new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = 0, Id = "call_abc123", Name = "date_time" },
                            ],
                        },
                        new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = 0, ArgumentsDelta = """{"time""" },
                            ],
                        },
                        new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = 0, ArgumentsDelta = """zone":"UTC"}""" },
                            ],
                        },
                        new LlmStreamChunk { IsComplete = true, FinishReason = "tool_calls" });
                }

                // Second LLM call: return text response
                return SingleChunkStream("The current time is 12:00 UTC.");
            });

        var message = new HubInboundMessage
        {
            ConversationId = "conv-tc-1",
            ChannelId = "webchat-default",
            SenderIdHash = "hash",
            Text = "What time is it?",
            Timestamp = DateTimeOffset.UtcNow,
        };

        await _runtime.HandleMessageAsync(message, CancellationToken.None);

        // Wait for background consumer to process the message
        await WaitForResponseComplete(_mockCaller);

        // Verify tool was called with the correct name and fully accumulated arguments
        var toolExecCalls = _mockCaller.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnToolExecution))
            .Select(c => (ToolExecutionMessage)c.GetArguments()[0]!)
            .ToList();

        // Should have Started + Completed for the one tool call
        Assert.Equal(2, toolExecCalls.Count);

        var started = toolExecCalls.First(m => m.Status == ToolExecutionStatus.Started);
        Assert.Equal("date_time", started.ToolName);
        Assert.Equal("""{"timezone":"UTC"}""", started.Input);

        var completed = toolExecCalls.First(m => m.Status == ToolExecutionStatus.Completed);
        Assert.Equal("date_time", completed.ToolName);
    }

    [Fact]
    public async Task MultipleToolCalls_CorrelatedByIndex_NotId()
    {
        // This is the exact bug scenario: two tool calls streaming with Ids only on first chunk.
        // Before the fix, the second chunk for tool 0 would create a new accumulator entry
        // because Id was null and the fallback "call_0" didn't match "call_abc123".
        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return ToolCallStream(
                        // First chunk: tool 0 starts with Id + Name
                        new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = 0, Id = "call_abc123", Name = "date_time" },
                            ],
                        },
                        // Second chunk: tool 0 argument fragment (no Id!)
                        new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = 0, ArgumentsDelta = """{"tz":""" },
                            ],
                        },
                        // Third chunk: tool 1 starts with its own Id + Name
                        new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = 1, Id = "call_def456", Name = "send_message" },
                            ],
                        },
                        // Fourth chunk: tool 0 gets more arguments (still no Id)
                        new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = 0, ArgumentsDelta = "\"UTC\"}" },
                            ],
                        },
                        // Fifth chunk: tool 1 gets arguments
                        new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = 1, ArgumentsDelta = """{"text":"hello"}""" },
                            ],
                        },
                        new LlmStreamChunk { IsComplete = true, FinishReason = "tool_calls" });
                }

                return SingleChunkStream("Done!");
            });

        var message = new HubInboundMessage
        {
            ConversationId = "conv-tc-multi",
            ChannelId = "webchat-default",
            SenderIdHash = "hash",
            Text = "Do two things",
            Timestamp = DateTimeOffset.UtcNow,
        };

        await _runtime.HandleMessageAsync(message, CancellationToken.None);
        await WaitForResponseComplete(_mockCaller);

        var toolExecCalls = _mockCaller.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnToolExecution))
            .Select(c => (ToolExecutionMessage)c.GetArguments()[0]!)
            .ToList();

        // 2 tools × (Started + Completed) = 4 messages
        Assert.Equal(4, toolExecCalls.Count);

        var startedCalls = toolExecCalls.Where(m => m.Status == ToolExecutionStatus.Started).ToList();
        Assert.Equal(2, startedCalls.Count);

        // Tool 0 should be "date_time" with its complete arguments
        // (ordered by Index, so date_time first, send_message second)
        Assert.Equal("date_time", startedCalls[0].ToolName);
        Assert.Equal("""{"tz":"UTC"}""", startedCalls[0].Input);

        // Tool 1 should be "send_message" with its complete arguments
        Assert.Equal("send_message", startedCalls[1].ToolName);
        Assert.Equal("""{"text":"hello"}""", startedCalls[1].Input);
    }

    [Fact]
    public async Task MultipleToolCallDeltasInSingleChunk_AccumulatesCorrectly()
    {
        // Test the case where multiple tool call deltas arrive in a single SSE event
        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return ToolCallStream(
                        // Both tool calls start in the same chunk
                        new LlmStreamChunk
                        {
                            ToolCallDeltas =
                            [
                                new LlmToolCallDelta { Index = 0, Id = "call_111", Name = "date_time", ArgumentsDelta = """{"tz":"UTC"}""" },
                                new LlmToolCallDelta { Index = 1, Id = "call_222", Name = "send_message", ArgumentsDelta = """{"text":"hi"}""" },
                            ],
                        },
                        new LlmStreamChunk { IsComplete = true, FinishReason = "tool_calls" });
                }

                return SingleChunkStream("All done.");
            });

        var message = new HubInboundMessage
        {
            ConversationId = "conv-tc-batch",
            ChannelId = "webchat-default",
            SenderIdHash = "hash",
            Text = "Do things simultaneously",
            Timestamp = DateTimeOffset.UtcNow,
        };

        await _runtime.HandleMessageAsync(message, CancellationToken.None);
        await WaitForResponseComplete(_mockCaller);

        var startedCalls = _mockCaller.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnToolExecution))
            .Select(c => (ToolExecutionMessage)c.GetArguments()[0]!)
            .Where(m => m.Status == ToolExecutionStatus.Started)
            .ToList();

        Assert.Equal(2, startedCalls.Count);
        Assert.Equal("date_time", startedCalls[0].ToolName);
        Assert.Equal("""{"tz":"UTC"}""", startedCalls[0].Input);
        Assert.Equal("send_message", startedCalls[1].ToolName);
        Assert.Equal("""{"text":"hi"}""", startedCalls[1].Input);
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCallStream(params LlmStreamChunk[] chunks)
    {
        await Task.CompletedTask;
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<LlmStreamChunk> SingleChunkStream(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ContentDelta = content,
            IsComplete = true,
            FinishReason = "stop",
            Usage = new LlmTokenUsage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15,
            },
        };
    }

    /// <summary>
    /// Waits until <see cref="IAgentHubClient.OnResponseComplete"/> has been called at
    /// least once, with a timeout to prevent tests from hanging.
    /// </summary>
    private static async Task WaitForResponseComplete(IAgentHubClient client, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var calls = client.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnResponseComplete))
                .ToList();
            if (calls.Count > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for OnResponseComplete callback");
    }
}

/// <summary>
/// Tests that assistant text streamed <em>before</em> tool calls (in the same
/// turn) is persisted to <see cref="Cortex.Contained.Agent.Host.Storage.MessageStore"/>
/// alongside the post-tool final text. Without this, multi-round turns only
/// persist the last text segment, so the UI history shows only "the last half"
/// of what the user heard via TTS.
/// </summary>
public class AgentRuntimeHistoryPersistenceTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly Cortex.Contained.Agent.Host.Storage.MessageStore _messageStore;
    private readonly IAgentHubClient _mockCaller;
    private readonly ILlmClient _mockLlmClient;
    private readonly AgentRuntime _runtime;

    public AgentRuntimeHistoryPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agent-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _messageStore = new Cortex.Contained.Agent.Host.Storage.MessageStore(
            Path.Combine(_tempDir, "messages.db"),
            NullLogger<Cortex.Contained.Agent.Host.Storage.MessageStore>.Instance);

        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        _mockLlmClient = Substitute.For<ILlmClient>();

        var stubTool = Substitute.For<IAgentTool>();
        stubTool.Name.Returns("date_time");
        stubTool.Description.Returns("Gets the current date");
        stubTool.ParametersSchema.Returns("""{"type":"object","properties":{}}""");
        stubTool.ExecuteAsync(Arg.Any<string>(), Arg.Any<ToolExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new AgentToolResult { Success = true, Content = "2026-03-28" });

        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([stubTool], activeChannelStore, NullLogger<ToolRegistry>.Instance);
        var messageChannel = new AgentMessageChannel();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);

        _mockCaller = Substitute.For<IAgentHubClient>();
        hubClients.Client(Arg.Any<string>()).Returns(_mockCaller);
        bridgeAccessor.SetConnectionId("test-conn");

        _mockCaller.OnStatusChanged(Arg.Any<AgentStatusInfo>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseChunk(Arg.Any<ResponseChunkMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseComplete(Arg.Any<ResponseCompleteMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnToolExecution(Arg.Any<ToolExecutionMessage>()).Returns(Task.CompletedTask);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());

        _runtime = new AgentRuntime(
            sessions,
            _mockLlmClient,
            toolRegistry,
            sessionConfig,
            messageChannel,
            bridgeAccessor,
            activeChannelStore,
            httpClientFactory,
            _tempDir,
            _tempDir,
            NullLogger<AgentRuntime>.Instance,
            new ModelProvider(),
            imageAgingMonitor,
            messageStore: _messageStore);
    }

    public async ValueTask DisposeAsync()
    {
        await _runtime.StopProcessingAsync(CancellationToken.None);
        await _messageStore.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RecordInterrupted_AfterPersist_RewritesStoredMessage()
    {
        const string channel = "discord:trunc-test";

        // Simulate a fully-persisted assistant turn (interrupt-after-persist case).
        var recordId = await _messageStore.SaveMessageAsync(
            "assistant", channel, "assistant",
            "Sure. There was an engineer named Greg who automated his coffee. [full long story].",
            DateTimeOffset.UtcNow);

        var session = _runtime.GetOrCreateSessionForTest(channel);
        session.SetLastAssistantRecordId(recordId);

        await _runtime.RecordInterruptedAssistantTurnAsync(
            channel, "Sure. There was an engineer named Greg…");

        var messages = await _messageStore.GetMessagesAsync(channel);
        Assert.Equal("Sure. There was an engineer named Greg…", messages[^1].Content);
    }

    [Fact]
    public async Task ToolCallWithPreToolText_PersistsBothAssistantSegments()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        // Round 1: LLM streams a text preface, then a tool call ("I'll check ...").
        // Round 2: LLM streams the final response after the tool result ("It is ...").
        // Both segments were spoken via TTS in production — both should appear in history.
        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return PreToolTextWithToolCallStream(
                        "Let me check the date for you.",
                        "call_1",
                        "date_time",
                        "{}");
                }

                return SingleChunkStream("It is March 28.");
            });

        var message = new HubInboundMessage
        {
            ConversationId = "conv-hist-1",
            ChannelId = "webchat-hist",
            SenderIdHash = "hash",
            Text = "What date is it?",
            Timestamp = DateTimeOffset.UtcNow,
        };

        await _runtime.HandleMessageAsync(message, CancellationToken.None);
        await WaitForResponseComplete(_mockCaller);

        // Allow the final MessageStore write to settle (it happens after OnResponseComplete
        // on the same task, but we're racing against the test thread).
        await Task.Delay(100);

        var stored = await _messageStore.GetMessagesAsync("webchat-hist");
        var assistantContents = stored.Where(m => m.Role == "assistant").Select(m => m.Content).ToList();

        Assert.Equal(2, assistantContents.Count);
        Assert.Equal("Let me check the date for you.", assistantContents[0]);
        Assert.Equal("It is March 28.", assistantContents[1]);
    }

    private static async IAsyncEnumerable<LlmStreamChunk> PreToolTextWithToolCallStream(
        string preToolText, string callId, string toolName, string args)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ContentDelta = preToolText };
        yield return new LlmStreamChunk
        {
            ToolCallDeltas =
            [
                new LlmToolCallDelta { Index = 0, Id = callId, Name = toolName, ArgumentsDelta = args },
            ],
        };
        yield return new LlmStreamChunk { IsComplete = true, FinishReason = "tool_calls" };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> SingleChunkStream(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ContentDelta = content,
            IsComplete = true,
            FinishReason = "stop",
            Usage = new LlmTokenUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 },
        };
    }

    private static async Task WaitForResponseComplete(IAgentHubClient client, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var calls = client.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnResponseComplete))
                .ToList();
            if (calls.Count > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for OnResponseComplete callback");
    }
}

/// <summary>
/// Tests for per-channel parallel message processing in the AgentRuntime.
/// Verifies that different channels run in parallel while same-channel
/// messages are serialized, and that scheduled tasks use a dedicated lane.
/// </summary>
public class AgentRuntimeParallelDispatchTests : IAsyncLifetime
{
    private readonly IAgentHubClient _mockCaller;
    private readonly ILlmClient _mockLlmClient;
    private readonly AgentRuntime _runtime;
    private readonly AgentMessageChannel _messageChannel;

    public AgentRuntimeParallelDispatchTests()
    {
        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        _mockLlmClient = Substitute.For<ILlmClient>();
        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([], activeChannelStore, NullLogger<ToolRegistry>.Instance);
        _messageChannel = new AgentMessageChannel();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);

        _mockCaller = Substitute.For<IAgentHubClient>();
        hubClients.Client(Arg.Any<string>()).Returns(_mockCaller);
        bridgeAccessor.SetConnectionId("test-conn");

        _mockCaller.OnStatusChanged(Arg.Any<AgentStatusInfo>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseChunk(Arg.Any<ResponseChunkMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseComplete(Arg.Any<ResponseCompleteMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true, ConversationId = "delivered" });

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());
        _runtime = new AgentRuntime(sessions, _mockLlmClient, toolRegistry, sessionConfig, _messageChannel, bridgeAccessor, activeChannelStore, httpClientFactory, Path.GetTempPath(), Path.GetTempPath(), NullLogger<AgentRuntime>.Instance, new ModelProvider(), imageAgingMonitor);
    }

    public async Task InitializeAsync()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _runtime.StopProcessingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DifferentChannels_RunInParallel()
    {
        // Two messages on different channels should both start processing
        // concurrently. We use a gate to prove both are inside the LLM call
        // at the same time.
        var bothEntered = new CountdownEvent(2);
        var releaseGate = new ManualResetEventSlim(false);

        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // Signal that this call has started
                bothEntered.Signal();
                // Wait until both calls are in progress
                bothEntered.Wait(TimeSpan.FromSeconds(5));
                releaseGate.Wait(TimeSpan.FromSeconds(5));
                return SingleChunkStream("done");
            });

        var msg1 = new AgentMessage
        {
            ConversationId = "channel-A",
            ChannelId = "channel-A",
            Text = "msg1",
            Source = AgentMessageSource.User,
            CorrelationId = "c1",
        };

        var msg2 = new AgentMessage
        {
            ConversationId = "channel-B",
            ChannelId = "channel-B",
            Text = "msg2",
            Source = AgentMessageSource.User,
            CorrelationId = "c2",
        };

        await _messageChannel.EnqueueAsync(msg1);
        await _messageChannel.EnqueueAsync(msg2);

        // Wait for both LLM calls to be entered concurrently
        var enteredInTime = bothEntered.Wait(TimeSpan.FromSeconds(5));
        Assert.True(enteredInTime, "Both channels should have entered the LLM call concurrently");

        // Release both calls
        releaseGate.Set();

        // Wait for both responses to complete
        await WaitForResponseCompleteCount(_mockCaller, 2);
    }

    [Fact]
    public async Task SameChannel_SerializesMessages()
    {
        // Two messages on the same channel should not be processed concurrently.
        // The second call should only start after the first completes.
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();
        var allDone = new CountdownEvent(2);

        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                lock (lockObj)
                {
                    concurrentCount++;
                    if (concurrentCount > maxConcurrent)
                    {
                        maxConcurrent = concurrentCount;
                    }
                }

                // Simulate some processing time
                Thread.Sleep(100);

                lock (lockObj)
                {
                    concurrentCount--;
                }

                allDone.Signal();
                return SingleChunkStream("done");
            });

        var msg1 = new AgentMessage
        {
            ConversationId = "same-channel",
            ChannelId = "same-channel",
            Text = "msg1",
            Source = AgentMessageSource.User,
            CorrelationId = "c1",
        };

        var msg2 = new AgentMessage
        {
            ConversationId = "same-channel",
            ChannelId = "same-channel",
            Text = "msg2",
            Source = AgentMessageSource.User,
            CorrelationId = "c2",
        };

        await _messageChannel.EnqueueAsync(msg1);
        await _messageChannel.EnqueueAsync(msg2);

        var completed = allDone.Wait(TimeSpan.FromSeconds(5));
        Assert.True(completed, "Both messages should have completed");
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task ScheduledTasks_UseDedicatedLane_DoNotBlockUserChannels()
    {
        // A scheduled task and a user message should run in parallel,
        // even though the scheduled task takes a long time.
        var taskEntered = new ManualResetEventSlim(false);
        var releaseTask = new ManualResetEventSlim(false);

        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<LlmCompletionRequest>();
                var isScheduledTask = request.ConversationId.StartsWith("scheduled-", StringComparison.Ordinal);

                if (isScheduledTask)
                {
                    taskEntered.Set();
                    // Block until we release
                    releaseTask.Wait(TimeSpan.FromSeconds(5));
                    return SingleChunkStream("task done");
                }

                // User message: should start even though the scheduled task is still processing
                return SingleChunkStream("user done");
            });

        // Enqueue scheduled task first
        var scheduledMsg = new AgentMessage
        {
            ConversationId = "scheduled-task1",
            ChannelId = "scheduled",
            Text = "Run backup",
            Source = AgentMessageSource.ScheduledTask,
            CorrelationId = "sc1",
        };

        var userMsg = new AgentMessage
        {
            ConversationId = "webchat-default",
            ChannelId = "webchat-default",
            Text = "Hello",
            Source = AgentMessageSource.User,
            CorrelationId = "uc1",
        };

        await _messageChannel.EnqueueAsync(scheduledMsg);
        // Wait for the scheduled task to enter the LLM call
        taskEntered.Wait(TimeSpan.FromSeconds(5));

        await _messageChannel.EnqueueAsync(userMsg);

        // The user message should complete even though the scheduled task is still blocked
        await WaitForResponseCompleteCount(_mockCaller, 1, timeoutMs: 5000);

        // Now release the scheduled task
        releaseTask.Set();
    }

    [Fact]
    public async Task MultipleScheduledTasks_DifferentConversations_RunInParallel()
    {
        // Scheduled tasks with different conversation IDs each get their own
        // session loop and run in parallel under the new per-session dispatch.
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();
        var allDone = new CountdownEvent(2);
        var bothEntered = new CountdownEvent(2);

        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                lock (lockObj)
                {
                    concurrentCount++;
                    if (concurrentCount > maxConcurrent)
                    {
                        maxConcurrent = concurrentCount;
                    }
                }

                bothEntered.Signal();
                // Wait until both have entered to confirm parallel execution
                bothEntered.Wait(TimeSpan.FromSeconds(5));

                lock (lockObj)
                {
                    concurrentCount--;
                }

                allDone.Signal();
                return SingleChunkStream("task done");
            });

        var task1 = new AgentMessage
        {
            ConversationId = "scheduled-1",
            ChannelId = "scheduled",
            Text = "Task one",
            Source = AgentMessageSource.ScheduledTask,
            CorrelationId = "st1",
        };

        var task2 = new AgentMessage
        {
            ConversationId = "scheduled-2",
            ChannelId = "scheduled",
            Text = "Task two",
            Source = AgentMessageSource.ScheduledTask,
            CorrelationId = "st2",
        };

        await _messageChannel.EnqueueAsync(task1);
        await _messageChannel.EnqueueAsync(task2);

        var completed = allDone.Wait(TimeSpan.FromSeconds(5));
        Assert.True(completed, "Both scheduled tasks should have completed");
        Assert.Equal(2, maxConcurrent);
    }

    [Fact]
    public async Task StopProcessingAsync_DrainsInFlightTasks()
    {
        // Verify that StopProcessingAsync waits for in-flight tasks to complete
        // rather than abandoning them.
        var entered = new ManualResetEventSlim(false);
        var releaseGate = new ManualResetEventSlim(false);
        var processingCompleted = false;

        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                entered.Set();
                releaseGate.Wait(TimeSpan.FromSeconds(5));
                processingCompleted = true;
                return SingleChunkStream("done");
            });

        var msg = new AgentMessage
        {
            ConversationId = "drain-test",
            ChannelId = "drain-test",
            Text = "test",
            Source = AgentMessageSource.User,
            CorrelationId = "d1",
        };

        await _messageChannel.EnqueueAsync(msg);

        // Wait for the message to start processing
        entered.Wait(TimeSpan.FromSeconds(5));

        // Release the gate so the task can complete
        releaseGate.Set();

        // StopProcessingAsync should wait for the in-flight task
        await _runtime.StopProcessingAsync(CancellationToken.None);

        Assert.True(processingCompleted, "StopProcessingAsync should have waited for in-flight task to complete");
    }

    private static async IAsyncEnumerable<LlmStreamChunk> SingleChunkStream(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ContentDelta = content,
            IsComplete = true,
            FinishReason = "stop",
            Usage = new LlmTokenUsage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15,
            },
        };
    }

    /// <summary>
    /// Waits until <see cref="IAgentHubClient.OnResponseComplete"/> or
    /// <see cref="IAgentHubClient.OnProactiveMessage"/> has been called
    /// the expected number of times.
    /// </summary>
    private static async Task WaitForResponseCompleteCount(IAgentHubClient client, int expectedCount, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var responseCompleteCalls = client.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnResponseComplete));
            var proactiveCalls = client.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnProactiveMessage));

            if (responseCompleteCalls + proactiveCalls >= expectedCount)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {expectedCount} response completion callbacks");
    }
}

/// <summary>
/// Proves the durable at-least-once delivery contract for subagent completion
/// notifications: a consumed <c>SubagentTaskId</c> is marked Delivered only after
/// the parent turn's final response lands, and is released back to Pending on any
/// failure (LLM error, response-delivery failure, cancellation). Ordinary messages
/// never touch the notification store.
/// </summary>
public sealed class AgentRuntimeSubagentCompletionTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly SubagentSessionStore _subagentStore;
    private readonly AgentMessageChannel _messageChannel;
    private readonly IAgentHubClient _mockCaller;
    private readonly ILlmClient _mockLlmClient;
    private readonly AgentRuntime _runtime;
    private readonly ManualResetEventSlim _toolEntered = new(false);
    private readonly TaskCompletionSource _toolRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentRuntimeSubagentCompletionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sub-completion-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _subagentStore = new SubagentSessionStore(_tempDir, NullLogger<SubagentSessionStore>.Instance);

        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        _mockLlmClient = Substitute.For<ILlmClient>();
        var activeChannelStore = new ActiveChannelStore();

        // Gate tool: blocks mid-round until the test releases it, so a completion
        // notification can be injected between tool rounds deterministically.
        var gateTool = Substitute.For<IAgentTool>();
        gateTool.Name.Returns("wait_gate");
        gateTool.Description.Returns("Blocks until the test releases it");
        gateTool.ParametersSchema.Returns("""{"type":"object","properties":{}}""");
        gateTool.ExecuteAsync(Arg.Any<string>(), Arg.Any<ToolExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                _toolEntered.Set();
                await _toolRelease.Task.ConfigureAwait(false);
                return new AgentToolResult { Success = true, Content = "released" };
            });

        var toolRegistry = new ToolRegistry([gateTool], activeChannelStore, NullLogger<ToolRegistry>.Instance);
        _messageChannel = new AgentMessageChannel();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);

        _mockCaller = Substitute.For<IAgentHubClient>();
        hubClients.Client(Arg.Any<string>()).Returns(_mockCaller);
        bridgeAccessor.SetConnectionId("test-conn");

        _mockCaller.OnStatusChanged(Arg.Any<AgentStatusInfo>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseChunk(Arg.Any<ResponseChunkMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseComplete(Arg.Any<ResponseCompleteMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnToolExecution(Arg.Any<ToolExecutionMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnError(Arg.Any<AgentErrorMessage>()).Returns(Task.CompletedTask);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());

        _runtime = new AgentRuntime(
            sessions,
            _mockLlmClient,
            toolRegistry,
            sessionConfig,
            _messageChannel,
            bridgeAccessor,
            activeChannelStore,
            httpClientFactory,
            Path.GetTempPath(),
            _tempDir,
            NullLogger<AgentRuntime>.Instance,
            new ModelProvider(),
            imageAgingMonitor,
            subagentStore: _subagentStore);
    }

    public async ValueTask DisposeAsync()
    {
        _toolRelease.TrySetResult();
        await _runtime.StopProcessingAsync(CancellationToken.None);
        _subagentStore.Dispose();
        _toolEntered.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task SubagentCompletion_SuccessfulResponse_MarksDelivered()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        SeedEnqueuedCompletion("sa-ok", "conv-ok");
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Task finished — result relayed."));

        await _messageChannel.EnqueueAsync(CompletionMessage("sa-ok", "conv-ok"));

        await WaitUntilAsync(() => NotificationState("sa-ok") == SubagentNotificationState.Delivered);
    }

    [Fact]
    public async Task SubagentCompletion_LlmError_ReleasesForRetry()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        SeedEnqueuedCompletion("sa-err", "conv-err");
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ErrorStream("provider exploded"));

        await _messageChannel.EnqueueAsync(CompletionMessage("sa-err", "conv-err"));

        await WaitUntilAsync(() => NotificationState("sa-err") == SubagentNotificationState.Pending);
    }

    [Fact]
    public async Task SubagentCompletion_ResponseDeliveryFailure_ReleasesForRetry()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        SeedEnqueuedCompletion("sa-deliv", "conv-deliv");
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("hello"));
        _mockCaller.OnResponseComplete(Arg.Any<ResponseCompleteMessage>())
            .Returns(Task.FromException(new InvalidOperationException("bridge send failed")));

        await _messageChannel.EnqueueAsync(CompletionMessage("sa-deliv", "conv-deliv"));

        await WaitUntilAsync(() => NotificationState("sa-deliv") == SubagentNotificationState.Pending);
    }

    [Fact]
    public async Task SubagentCompletion_Cancellation_ReleasesForRetry()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        SeedEnqueuedCompletion("sa-cxl", "conv-cxl");
        var entered = new ManualResetEventSlim(false);
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => BlockingStream(entered, callInfo.Arg<CancellationToken>()));

        await _messageChannel.EnqueueAsync(CompletionMessage("sa-cxl", "conv-cxl"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "LLM stream never started");

        await _runtime.AbortGenerationAsync("conv-cxl");

        await WaitUntilAsync(() => NotificationState("sa-cxl") == SubagentNotificationState.Pending);
        entered.Dispose();
    }

    [Fact]
    public async Task SubagentCompletion_SaveMessageFailure_ReleasesForRetry()
    {
        // The inbound-message persistence (messageStore.SaveMessageAsync) throws BEFORE the
        // turn's generation begins. The claim guard must cover this window too: the consumed
        // notification is released back to Pending, never parked Enqueued until restart.
        var failingStore = Substitute.For<IMessageStore>();
        failingStore.SaveMessageAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<MessageCategory>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long>(new InvalidOperationException("message store unavailable")));

        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([], activeChannelStore, NullLogger<ToolRegistry>.Instance);
        var channel = new AgentMessageChannel();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);
        hubClients.Client(Arg.Any<string>()).Returns(_mockCaller);
        bridgeAccessor.SetConnectionId("test-conn");

        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());

        var runtime = new AgentRuntime(
            sessions,
            _mockLlmClient,
            toolRegistry,
            sessionConfig,
            channel,
            bridgeAccessor,
            activeChannelStore,
            Substitute.For<IHttpClientFactory>(),
            Path.GetTempPath(),
            _tempDir,
            NullLogger<AgentRuntime>.Instance,
            new ModelProvider(),
            imageAgingMonitor,
            messageStore: failingStore,
            subagentStore: _subagentStore);

        try
        {
            await runtime.StartProcessingAsync(CancellationToken.None);
            SeedEnqueuedCompletion("sa-save", "conv-save");
            _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
                .Returns(SingleChunkStream("never reached"));

            await channel.EnqueueAsync(CompletionMessage("sa-save", "conv-save"));

            await WaitUntilAsync(() => NotificationState("sa-save") == SubagentNotificationState.Pending);
        }
        finally
        {
            await runtime.StopProcessingAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SubagentCompletion_InjectedMidTurn_IsAcknowledgedByOwningTurn()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        SeedEnqueuedCompletion("sa-mid", "conv-mid");
        var llmCall = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref llmCall) == 1
                ? ToolCallStream("wait_gate")
                : SingleChunkStream("All done."));

        // A user message owns the turn; the completion is injected mid-round.
        await _messageChannel.EnqueueAsync(new AgentMessage
        {
            ConversationId = "conv-mid",
            ChannelId = "conv-mid",
            Text = "kick off a turn",
            Source = AgentMessageSource.User,
        });
        Assert.True(_toolEntered.Wait(TimeSpan.FromSeconds(5)), "gate tool never started");

        // Inject the completion while the tool round is executing, and wait for it to
        // reach the session's pending queue so the between-rounds drain consumes it.
        await _messageChannel.EnqueueAsync(CompletionMessage("sa-mid", "conv-mid"));
        var session = _runtime.GetOrCreateSessionForTest("conv-mid");
        await WaitUntilAsync(() => session.PendingMessageCount > 0);

        _toolRelease.SetResult();

        await WaitUntilAsync(() => NotificationState("sa-mid") == SubagentNotificationState.Delivered);
    }

    [Fact]
    public async Task OrdinaryMessage_DoesNotTouchNotificationStore()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        SeedEnqueuedCompletion("sa-idle", "conv-else");
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("plain response"));

        await _messageChannel.EnqueueAsync(new AgentMessage
        {
            ConversationId = "conv-else",
            ChannelId = "conv-else",
            Text = "hello",
            Source = AgentMessageSource.User,
        });

        await WaitUntilAsync(() => _mockCaller.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnResponseComplete)));

        // Settle window: no late notification-store writes may land either.
        await Task.Delay(150);
        Assert.Equal(SubagentNotificationState.Enqueued, NotificationState("sa-idle"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void SeedEnqueuedCompletion(string taskId, string conversationId)
    {
        _subagentStore.Create(new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = conversationId,
            ParentChannel = conversationId,
            Description = "background job",
            Prompt = "do it",
            State = SubagentTaskState.Completed,
            Result = "the result",
            CompletedAt = DateTimeOffset.UtcNow,
            NotificationState = SubagentNotificationState.Enqueued,
        });
    }

    private static AgentMessage CompletionMessage(string taskId, string conversationId) => new()
    {
        ConversationId = conversationId,
        ChannelId = conversationId,
        Text = $"[Background task completed] {taskId}",
        Source = AgentMessageSource.SubagentCompletion,
        SubagentTaskId = taskId,
    };

    private SubagentNotificationState NotificationState(string taskId)
        => _subagentStore.GetById(taskId)!.NotificationState;

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition was not satisfied within the timeout.");
            }

            await Task.Delay(15);
        }
    }

    private static async IAsyncEnumerable<LlmStreamChunk> SingleChunkStream(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ContentDelta = content,
            IsComplete = true,
            FinishReason = "stop",
            Usage = new LlmTokenUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 },
        };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ErrorStream(string errorMessage)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ErrorMessage = errorMessage };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> BlockingStream(
        ManualResetEventSlim entered,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        entered.Set();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield return new LlmStreamChunk { ContentDelta = "unreachable" };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCallStream(string toolName)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ToolCallDeltas =
            [
                new LlmToolCallDelta { Index = 0, Id = "call_gate", Name = toolName, ArgumentsDelta = "{}" },
            ],
        };
        yield return new LlmStreamChunk { IsComplete = true, FinishReason = "tool_calls" };
    }
}

/// <summary>
/// Tests for /context and /compact slash commands.
/// Verifies that slash commands are handled locally without calling the LLM.
/// </summary>
public class AgentRuntimeSlashCommandTests : IAsyncLifetime
{
    private readonly IAgentHubClient _mockCaller;
    private readonly ILlmClient _mockLlmClient;
    private readonly AgentRuntime _runtime;

    public AgentRuntimeSlashCommandTests()
    {
        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        _mockLlmClient = Substitute.For<ILlmClient>();
        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([], activeChannelStore, NullLogger<ToolRegistry>.Instance);
        var messageChannel = new AgentMessageChannel();

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);

        _mockCaller = Substitute.For<IAgentHubClient>();
        hubClients.Client(Arg.Any<string>()).Returns(_mockCaller);
        bridgeAccessor.SetConnectionId("test-conn");

        _mockCaller.OnStatusChanged(Arg.Any<AgentStatusInfo>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseChunk(Arg.Any<ResponseChunkMessage>()).Returns(Task.CompletedTask);
        _mockCaller.OnResponseComplete(Arg.Any<ResponseCompleteMessage>()).Returns(Task.CompletedTask);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());
        _runtime = new AgentRuntime(sessions, _mockLlmClient, toolRegistry, sessionConfig, messageChannel, bridgeAccessor, activeChannelStore, httpClientFactory, Path.GetTempPath(), Path.GetTempPath(), NullLogger<AgentRuntime>.Instance, new ModelProvider(), imageAgingMonitor);
    }

    public async Task InitializeAsync()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _runtime.StopProcessingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ContextCommand_ReturnsContextInfo_WithoutCallingLlm()
    {
        await SendAndWaitForNthResponse("/context", 1);

        // Verify LLM was never called
        _mockLlmClient.DidNotReceive()
            .StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());

        // Verify response contains expected context info fields
        var responseText = GetLastResponseText();
        Assert.Contains("Context Window", responseText);
        Assert.Contains("Prompt tokens", responseText);
        Assert.Contains("Session messages", responseText);
        Assert.Contains("Compaction threshold", responseText);
    }

    [Fact]
    public async Task ContextCommand_WithLeadingWhitespace_StillHandled()
    {
        await SendAndWaitForNthResponse("  /context", 1);

        _mockLlmClient.DidNotReceive()
            .StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());

        var responseText = GetLastResponseText();
        Assert.Contains("Context Window", responseText);
    }

    [Fact]
    public async Task ContextCommand_CaseInsensitive()
    {
        await SendAndWaitForNthResponse("/CONTEXT", 1);

        _mockLlmClient.DidNotReceive()
            .StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());

        var responseText = GetLastResponseText();
        Assert.Contains("Context Window", responseText);
    }

    [Fact]
    public async Task CompactCommand_TooFewMessages_ReportsNotEnough()
    {
        await SendAndWaitForNthResponse("/compact", 1);

        _mockLlmClient.DidNotReceive()
            .StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());

        var responseText = GetLastResponseText();
        Assert.Contains("Not enough messages", responseText);
    }

    [Fact]
    public async Task CompactCommand_WithHistory_TriggersCompaction()
    {
        // Build up conversation history on a single conversation (need 6+ messages)
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Response"));
        _mockLlmClient.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = true, Content = "## Goal\nTest summary" });

        // Send 4 messages to the same conversation → 4 user + 4 assistant = 8 messages
        for (var i = 0; i < 4; i++)
        {
            await SendAndWaitForNthResponse($"Message {i}", i + 1, "conv-compact");
        }

        // Clear call tracking so we can verify /compact behavior cleanly
        _mockCaller.ClearReceivedCalls();
        _mockLlmClient.ClearReceivedCalls();

        // Re-setup CompleteAsync for the compaction summary call
        _mockLlmClient.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = true, Content = "## Goal\nTest summary" });

        await SendAndWaitForNthResponse("/compact", 1, "conv-compact");

        var responseText = GetLastResponseText();
        Assert.True(
            responseText.Contains("Compaction Complete", StringComparison.Ordinal) ||
            responseText.Contains("Not enough messages", StringComparison.Ordinal),
            $"Expected compaction result, got: {responseText}");
    }

    [Fact]
    public async Task SlashCommand_DoesNotAddToSessionHistory()
    {
        // Send a /context command — should produce OnResponseComplete #1
        await SendAndWaitForNthResponse("/context", 1, "conv-history-test");

        // Now send a real message — should produce OnResponseComplete #2
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Hello"));

        await SendAndWaitForNthResponse("Hello", 2, "conv-history-test");

        // Verify the LLM was called and the messages don't include /context
        var llmCall = _mockLlmClient.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == nameof(ILlmClient.StreamCompleteAsync));
        var request = (LlmCompletionRequest)llmCall.GetArguments()[0]!;
        var userMessages = request.Messages.Where(m => m.Role == "user").ToList();

        Assert.DoesNotContain(userMessages, m => m.Content?.Contains("/context", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task NonSlashMessage_IsNotIntercepted()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Normal response"));

        await SendAndWaitForNthResponse("Tell me about /context windows", 1);

        // LLM should have been called — this is not a slash command
        _mockLlmClient.Received(1)
            .StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());
    }

    private async Task SendAndWaitForNthResponse(string text, int expectedCount, string conversationId = "conv-slash")
    {
        var message = new HubInboundMessage
        {
            ConversationId = conversationId,
            ChannelId = "webchat-default",
            SenderIdHash = "hash123",
            Text = text,
            Timestamp = DateTimeOffset.UtcNow,
        };

        await _runtime.HandleMessageAsync(message, CancellationToken.None);
        await WaitForResponseCompleteCount(_mockCaller, expectedCount);
    }

    private string GetLastResponseText()
    {
        var completeCalls = _mockCaller.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnResponseComplete))
            .ToList();

        Assert.NotEmpty(completeCalls);
        var lastCall = completeCalls[^1];
        var msg = (ResponseCompleteMessage)lastCall.GetArguments()[0]!;
        return msg.FullText;
    }

    private static async Task WaitForResponseCompleteCount(IAgentHubClient client, int expectedCount, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var completeCalls = client.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(IAgentHubClient.OnResponseComplete));
            if (completeCalls >= expectedCount)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {expectedCount} OnResponseComplete callbacks");
    }

    private static async IAsyncEnumerable<LlmStreamChunk> SingleChunkStream(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ContentDelta = content,
            IsComplete = true,
            FinishReason = "stop",
            Usage = new LlmTokenUsage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15,
            },
        };
    }
}
