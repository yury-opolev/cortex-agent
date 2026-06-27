using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class AgentLoopTests
{
    private readonly ILlmClient _mockLlmClient = Substitute.For<ILlmClient>();

    private static ToolRegistry CreateRegistry(params IAgentTool[] tools)
        => new(tools, new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

    private AgentLoop CreateLoop(params IAgentTool[] tools)
        => new(_mockLlmClient, CreateRegistry(tools), NullLogger<AgentLoop>.Instance);

    private static AgentLoopConfig DefaultConfig(string conversationId = "test-conv") => new()
    {
        Model = "gpt-4o",
        ConversationId = conversationId,
        ChannelId = "test-channel",
        MaxRounds = 50,
    };

    // ── Core loop behavior ───────────────────────────────────────���───────

    [Fact]
    public async Task ExecuteAsync_NoToolCalls_ReturnsCompleted()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("The answer is 42."));

        var loop = CreateLoop();
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are helpful." },
            new() { Role = "user", Content = "What is the answer?" },
        };
        var callbacks = new TestCallbacks(messages);

        var result = await loop.ExecuteAsync(DefaultConfig(), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Completed, result.Outcome);
        Assert.Equal("The answer is 42.", result.ResponseText);
        Assert.Equal(1, result.RoundsExecuted);
        Assert.True(callbacks.LoopCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_WithToolCalls_ExecutesToolsAndContinues()
    {
        var tool = new FakeTool("date_time", "Gets date", "2026-03-28");

        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? ToolCallStream("call_1", "date_time", "{}")
                    : SingleChunkStream("Today is March 28.");
            });

        var loop = CreateLoop(tool);
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "What date is it?" },
        };
        var callbacks = new TestCallbacks(messages);

        var result = await loop.ExecuteAsync(DefaultConfig(), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Completed, result.Outcome);
        Assert.Equal("Today is March 28.", result.ResponseText);
        Assert.Equal(2, result.RoundsExecuted);
        Assert.Equal(1, callbacks.ToolsStarted);
        Assert.Equal(1, callbacks.ToolsCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_Error_ReturnsError()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ErrorStream("Rate limit exceeded"));

        var loop = CreateLoop();
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Hello" },
        };
        var callbacks = new TestCallbacks(messages);

        var result = await loop.ExecuteAsync(DefaultConfig(), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Error, result.Outcome);
        Assert.Contains("Rate limit", result.ErrorMessage!);
        Assert.True(callbacks.ErrorReceived);
    }

    [Fact]
    public async Task ExecuteAsync_MaxRounds_ReturnsMaxRoundsExceeded()
    {
        // Always return tool calls with different args to avoid doom loop detection
        var roundCounter = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToolCallStream($"call_{roundCounter}", "date_time", $"{{\"round\":{roundCounter++}}}"));

        var tool = new FakeTool("date_time", "Gets date", "2026-03-28");
        var loop = CreateLoop(tool);
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Loop forever" },
        };
        var callbacks = new TestCallbacks(messages);
        var config = DefaultConfig() with { MaxRounds = 3 };

        var result = await loop.ExecuteAsync(config, callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.MaxRoundsExceeded, result.Outcome);
        Assert.Equal(3, result.RoundsExecuted);
    }

    [Fact]
    public async Task ExecuteAsync_ContentDelta_CallbacksFire()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(MultiChunkStream("Hello ", "world!"));

        var loop = CreateLoop();
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Hi" },
        };
        var callbacks = new TestCallbacks(messages);

        await loop.ExecuteAsync(DefaultConfig(), callbacks, CancellationToken.None);

        Assert.Equal(2, callbacks.ContentDeltas.Count);
        Assert.Equal("Hello ", callbacks.ContentDeltas[0]);
        Assert.Equal("world!", callbacks.ContentDeltas[1]);
    }

    [Fact]
    public async Task ExecuteAsync_InjectMessage_AppearsInNextRound()
    {
        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                    return ToolCallStream("call_1", "date_time", "{}");

                var request = (LlmCompletionRequest)callInfo[0];
                var hasInjected = request.Messages.Any(m => m.Role == "user" && m.Content == "Also check tests/");
                return SingleChunkStream(hasInjected ? "Found injected." : "No injection.");
            });

        var tool = new FakeTool("date_time", "Gets date", "now");
        var loop = CreateLoop(tool);
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Start" },
        };
        var callbacks = new TestCallbacks(messages);

        callbacks.InjectMessage("Also check tests/");

        var result = await loop.ExecuteAsync(DefaultConfig(), callbacks, CancellationToken.None);

        Assert.Equal("Found injected.", result.ResponseText);
    }

    [Fact]
    public async Task ExecuteAsync_ToolExclusions_Respected()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Done."));

        var tool = new FakeTool("allowed_tool", "Allowed", "ok");
        var excluded = new FakeTool("blocked_tool", "Blocked", "nope");
        var loop = CreateLoop(tool, excluded);
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Go" },
        };
        var callbacks = new TestCallbacks(messages);
        var config = DefaultConfig() with
        {
            ExcludedTools = System.Collections.Frozen.FrozenSet.ToFrozenSet(
                ["blocked_tool"], StringComparer.OrdinalIgnoreCase),
        };

        await loop.ExecuteAsync(config, callbacks, CancellationToken.None);

        var llmCall = _mockLlmClient.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == nameof(ILlmClient.StreamCompleteAsync));
        var request = (LlmCompletionRequest)llmCall.GetArguments()[0]!;
        Assert.NotNull(request.Tools);
        Assert.Single(request.Tools);
        Assert.Equal("allowed_tool", request.Tools[0].Name);
    }

    [Fact]
    public async Task ExecuteAsync_OnRoundComplete_CalledAfterToolExecution()
    {
        var tool = new FakeTool("my_tool", "Tool", "result");
        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? ToolCallStream("call_1", "my_tool", "{}")
                    : SingleChunkStream("Done.");
            });

        var loop = CreateLoop(tool);
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Go" },
        };
        var callbacks = new TestCallbacks(messages);

        await loop.ExecuteAsync(DefaultConfig(), callbacks, CancellationToken.None);

        Assert.Equal(1, callbacks.RoundsCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_ContextOverflow_RecoveryRetries()
    {
        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? ErrorStream("context_length_exceeded: max 128000 tokens")
                    : SingleChunkStream("Recovered!");
            });

        var loop = CreateLoop();
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Big request" },
        };
        var callbacks = new TestCallbacks(messages) { RecoverFromOverflow = true };

        var result = await loop.ExecuteAsync(DefaultConfig(), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Completed, result.Outcome);
        Assert.Equal("Recovered!", result.ResponseText);
        Assert.True(callbacks.OverflowRecovered);
    }

    // ── Test doubles ─────────────────────────────────────────────────────

    private sealed class TestCallbacks : IAgentLoopCallbacks
    {
        private readonly List<LlmMessage> _messages;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _injected = new();

        public TestCallbacks(List<LlmMessage> messages) => _messages = messages;

        public bool LoopCompleted { get; private set; }
        public bool ErrorReceived { get; private set; }
        public bool OverflowRecovered { get; private set; }
        public bool RecoverFromOverflow { get; set; }
        public int ToolsStarted { get; private set; }
        public int ToolsCompleted { get; private set; }
        public int RoundsCompleted { get; private set; }
        public List<string> ContentDeltas { get; } = [];

        public void InjectMessage(string message) => _injected.Enqueue(message);

        public Task<List<LlmMessage>> PrepareMessagesAsync(int round, CancellationToken cancellationToken)
            => Task.FromResult<List<LlmMessage>>([.. _messages]);
        public void DrainInjectedMessages()
        {
            while (_injected.TryDequeue(out var msg))
            {
                _messages.Add(new LlmMessage { Role = "user", Content = msg });
            }
        }
        public Task OnContentDeltaAsync(string delta, int seq, CancellationToken ct) { ContentDeltas.Add(delta); return Task.CompletedTask; }
        public Task OnToolStartAsync(LlmToolCall tc, CancellationToken ct) { ToolsStarted++; return Task.CompletedTask; }
        public Task OnToolCompleteAsync(LlmToolCall tc, AgentToolResult r, TimeSpan d, CancellationToken ct) { ToolsCompleted++; return Task.CompletedTask; }
        public Task OnRoundCompleteAsync(int round, LlmTokenUsage? usage, CancellationToken ct) { RoundsCompleted++; return Task.CompletedTask; }
        public Task<bool> OnContextOverflowAsync(string err, CancellationToken ct) { OverflowRecovered = RecoverFromOverflow; return Task.FromResult(RecoverFromOverflow); }
        public Task OnErrorAsync(string err, CancellationToken ct) { ErrorReceived = true; return Task.CompletedTask; }
        public Task OnDoomLoopAsync(string tool, CancellationToken ct) => Task.CompletedTask;
        public Task OnLoopCompleteAsync(AgentLoopResult result, CancellationToken ct) { LoopCompleted = true; return Task.CompletedTask; }
        public void OnAssistantMessage(LlmMessage msg) => _messages.Add(msg);
        public void OnToolResultMessage(LlmMessage msg) => _messages.Add(msg);
    }

    private sealed class FakeTool : IAgentTool
    {
        private readonly string _result;
        public FakeTool(string name, string description, string result) { Name = name; Description = description; _result = result; }
        public string Name { get; }
        public string Description { get; }
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public Task<AgentToolResult> ExecuteAsync(string args, ToolExecutionContext ctx, CancellationToken ct)
            => Task.FromResult(new AgentToolResult { Success = true, Content = _result });
    }

    // ── Stream helpers ───────────────────────────────────────────────────

    private static async IAsyncEnumerable<LlmStreamChunk> SingleChunkStream(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ContentDelta = content, IsComplete = true, FinishReason = "stop",
            Usage = new LlmTokenUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 },
        };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> MultiChunkStream(params string[] chunks)
    {
        await Task.CompletedTask;
        for (var i = 0; i < chunks.Length; i++)
        {
            yield return new LlmStreamChunk
            {
                ContentDelta = chunks[i],
                IsComplete = i == chunks.Length - 1,
                FinishReason = i == chunks.Length - 1 ? "stop" : null,
                Usage = i == chunks.Length - 1
                    ? new LlmTokenUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 }
                    : null,
            };
        }
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ErrorStream(string error)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ErrorMessage = error };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCallStream(string callId, string toolName, string args)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ToolCallDeltas = [new LlmToolCallDelta { Index = 0, Id = callId, Name = toolName, ArgumentsDelta = args }],
            IsComplete = true, FinishReason = "tool_calls",
        };
    }
}
