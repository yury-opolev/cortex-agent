using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Llm.Providers;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Mid-stream transient-fault recovery in <see cref="AgentLoop"/>.
/// <para>
/// Root cause this closes (2026-08-15): <c>DirectLlmClient</c>'s same-provider retry AND its
/// provider failover are both gated on <c>!emittedAny</c> — pre-content only. A stall detected
/// by <see cref="LlmStreamIdleGuard"/> AFTER the first chunk therefore reached the caller as a
/// terminal error that nothing could retry, killing a 25-minute subagent run outright. The
/// facade cannot retry post-content (it cannot un-stream), but the loop can: it re-issues the
/// whole round from the unchanged message history.
/// </para>
/// <para>
/// This is safe only because <see cref="AgentLoop"/> is used exclusively by subagents, whose
/// <c>OnContentDeltaAsync</c> is a no-op — no partial text has been shown to anyone. The retry
/// budget is configuration-driven (<see cref="AgentLoopConfig.MaxTransientStreamRetries"/>) so a
/// future user-facing consumer can disable it by setting 0.
/// </para>
/// </summary>
public class AgentLoopTransientRetryTests
{
    private readonly ILlmClient llmClient = Substitute.For<ILlmClient>();

    private AgentLoop CreateLoop(params IAgentTool[] tools)
        => new(
            this.llmClient,
            new ToolRegistry(tools, new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance),
            NullLogger<AgentLoop>.Instance);

    private static AgentLoopConfig Config(int retries = 2) => new()
    {
        Model = "claude-opus-5",
        ConversationId = "subagent-sa-abc",
        ChannelId = "subagent-sa-abc",
        MaxRounds = 10,
        MaxTransientStreamRetries = retries,
    };

    private static List<LlmMessage> Messages() =>
    [
        new() { Role = "system", Content = "You are a subagent." },
        new() { Role = "user", Content = "Do deep research." },
    ];

    /// <summary>The exact production shape: content, then the watchdog's transient fault.</summary>
    private static async IAsyncEnumerable<LlmStreamChunk> ContentThenTransientFault(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ContentDelta = content };
        yield return new LlmStreamChunk
        {
            IsComplete = true,
            ErrorMessage = LlmStreamFault.TransientPrefix + "LLM stream produced no data for 300s.",
        };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ContentThenTerminalFault(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ContentDelta = content };
        yield return new LlmStreamChunk
        {
            IsComplete = true,
            ErrorMessage = LlmStreamFault.TerminalPrefix + "mapper blew up",
        };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> Final(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ContentDelta = content,
            IsComplete = true,
            FinishReason = "stop",
        };
    }

    // ── The bug ────────────────────────────────────────────────────────

    [Fact]
    public async Task MidStreamTransientFault_AfterContent_IsRetriedNotFatal()
    {
        var calls = 0;
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls == 1
                    ? ContentThenTransientFault("Let me start by ")
                    : Final("The full answer.");
            });

        var callbacks = new RecordingCallbacks(Messages());
        var result = await this.CreateLoop().ExecuteAsync(Config(), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Completed, result.Outcome);
        Assert.Equal("The full answer.", result.ResponseText);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MidStreamTransientFault_PartialTextIsDiscardedNotConcatenated()
    {
        var calls = 0;
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls == 1 ? ContentThenTransientFault("HALF") : Final("WHOLE");
            });

        var callbacks = new RecordingCallbacks(Messages());
        var result = await this.CreateLoop().ExecuteAsync(Config(), callbacks, CancellationToken.None);

        Assert.Equal("WHOLE", result.ResponseText);
        Assert.DoesNotContain("HALF", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MidStreamTransientFault_DoesNotBurnARoundOrPollutHistory()
    {
        var calls = 0;
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls == 1 ? ContentThenTransientFault("partial") : Final("done");
            });

        var messages = Messages();
        var callbacks = new RecordingCallbacks(messages);
        var result = await this.CreateLoop().ExecuteAsync(Config(), callbacks, CancellationToken.None);

        // A retried attempt is the SAME round re-issued: no extra round counted, no partial
        // assistant message appended, and OnRoundCompleteAsync not fired for the dead attempt.
        Assert.Equal(1, result.RoundsExecuted);
        Assert.Equal(2, messages.Count);
        Assert.Equal(0, callbacks.RoundsCompleted);
    }

    [Fact]
    public async Task MidStreamTransientFault_IsNotReportedToOnErrorWhenItSucceedsOnRetry()
    {
        var calls = 0;
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls == 1 ? ContentThenTransientFault("partial") : Final("done");
            });

        var callbacks = new RecordingCallbacks(Messages());
        await this.CreateLoop().ExecuteAsync(Config(), callbacks, CancellationToken.None);

        Assert.False(callbacks.ErrorReceived);
    }

    // ── Bounds ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MidStreamTransientFault_ExhaustingTheBudget_FailsWithTheLastError()
    {
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ContentThenTransientFault("partial"));

        var callbacks = new RecordingCallbacks(Messages());
        var result = await this.CreateLoop().ExecuteAsync(Config(retries: 2), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Error, result.Outcome);
        Assert.Contains("produced no data", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.True(callbacks.ErrorReceived);

        // 1 initial attempt + 2 retries.
        Assert.Equal(3, this.StreamCallCount());
    }

    [Fact]
    public async Task MaxTransientStreamRetriesZero_DisablesTheRetryEntirely()
    {
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ContentThenTransientFault("partial"));

        var callbacks = new RecordingCallbacks(Messages());
        var result = await this.CreateLoop().ExecuteAsync(Config(retries: 0), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Error, result.Outcome);
        Assert.Equal(1, this.StreamCallCount());
    }

    [Fact]
    public async Task RetryBudget_IsPerRoundNotPerLoop()
    {
        // Round 1 stalls once then yields a tool call; round 2 stalls once then finishes.
        // A per-loop budget would still pass here, so also assert both stalls were retried.
        var calls = 0;
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls switch
                {
                    1 => ContentThenTransientFault("a"),
                    2 => ToolCallStream("call_1", "noop", "{}"),
                    3 => ContentThenTransientFault("b"),
                    _ => Final("finished"),
                };
            });

        var callbacks = new RecordingCallbacks(Messages());
        var result = await this.CreateLoop(new NoopTool())
            .ExecuteAsync(Config(retries: 1), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Completed, result.Outcome);
        Assert.Equal("finished", result.ResponseText);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task MidStreamTransientFault_DiscardsHalfStreamedToolCallArguments()
    {
        // The corrupting leak: if the dead attempt's tool-call accumulators survived, its
        // partial JSON would be concatenated onto the retry's arguments and the loop would
        // execute a malformed tool call. Worse than duplicated text, and silent.
        var calls = 0;
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls switch
                {
                    1 => PartialToolCallThenTransientFault(),
                    2 => ToolCallStream("call_ok", "echo", """{"text":"hello"}"""),
                    _ => Final("done"),
                };
            });

        var tool = new RecordingTool();
        var result = await this.CreateLoop(tool)
            .ExecuteAsync(Config(), new RecordingCallbacks(Messages()), CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Completed, result.Outcome);
        var arguments = Assert.Single(tool.Invocations);
        Assert.Equal("""{"text":"hello"}""", arguments);
        Assert.DoesNotContain("truncated", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextOverflowRecovery_ThenATransientFault_BothRecoverInTheSameLoop()
    {
        // The three-way interaction between retryAfterRecovery, the transient retry and the
        // terminal error result, which nothing else exercises together.
        var calls = 0;
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls switch
                {
                    1 => ErrorStream("context_length_exceeded: max 200000 tokens"),
                    2 => ContentThenTransientFault("partial"),
                    _ => Final("recovered and retried"),
                };
            });

        var callbacks = new RecordingCallbacks(Messages()) { RecoverFromOverflow = true };
        var result = await this.CreateLoop().ExecuteAsync(Config(), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Completed, result.Outcome);
        Assert.Equal("recovered and retried", result.ResponseText);
        Assert.True(callbacks.OverflowRecovered);
        Assert.False(callbacks.ErrorReceived);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task EachAttempt_GetsItsOwnRequestId()
    {
        // Otherwise every attempt - and every stall report raised by one - shares a correlation
        // id, and the telemetry added alongside this retry cannot tell them apart.
        var calls = 0;
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls == 1 ? ContentThenTransientFault("partial") : Final("done");
            });

        await this.CreateLoop().ExecuteAsync(Config(), new RecordingCallbacks(Messages()), CancellationToken.None);

        var requestIds = this.llmClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILlmClient.StreamCompleteAsync))
            .Select(c => ((LlmCompletionRequest)c.GetArguments()[0]!).RequestId)
            .ToList();

        Assert.Equal(2, requestIds.Count);
        Assert.Equal(2, requestIds.Distinct(StringComparer.Ordinal).Count());
    }
    // ── Faults that must NOT be retried ────────────────────────────────

    [Fact]
    public async Task TerminalPrefixedFault_IsNotRetried()
    {
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ContentThenTerminalFault("partial"));

        var callbacks = new RecordingCallbacks(Messages());
        var result = await this.CreateLoop().ExecuteAsync(Config(), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Error, result.Outcome);
        Assert.Equal(1, this.StreamCallCount());
    }

    [Fact]
    public async Task PlainProviderError_IsNotRetried()
    {
        // "All providers failed: ..." and HTTP errors already exhausted the facade's own
        // retry/failover. Retrying them here would multiply cost for nothing.
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ErrorStream("HTTP 401: bad credentials"));

        var callbacks = new RecordingCallbacks(Messages());
        var result = await this.CreateLoop().ExecuteAsync(Config(), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Error, result.Outcome);
        Assert.Equal(1, this.StreamCallCount());
    }

    [Fact]
    public async Task ContextOverflow_StillTakesTheOverflowPathNotTheRetryPath()
    {
        var calls = 0;
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                return calls == 1
                    ? ErrorStream("context_length_exceeded: max 200000 tokens")
                    : Final("recovered");
            });

        var callbacks = new RecordingCallbacks(Messages()) { RecoverFromOverflow = true };
        var result = await this.CreateLoop().ExecuteAsync(Config(), callbacks, CancellationToken.None);

        Assert.Equal(AgentLoopOutcome.Completed, result.Outcome);
        Assert.True(callbacks.OverflowRecovered);
    }

    [Fact]
    public async Task Cancellation_DuringRetryBackoff_IsHonoured()
    {
        using var cts = new CancellationTokenSource();
        this.llmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return ContentThenTransientFault("partial");
            });

        var callbacks = new RecordingCallbacks(Messages());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => this.CreateLoop().ExecuteAsync(Config(), callbacks, cts.Token));
    }

    // ── Defaults ───────────────────────────────────────────────────────

    [Fact]
    public void DefaultConfig_DoesNotRetry_SoAConsumerMustOptInDeliberately()
    {
        // Re-issuing a round is only safe for a consumer that DISCARDS content deltas. That
        // cannot be checked here, so the default must be the safe one: a future user-facing
        // consumer inherits "off" by saying nothing.
        var config = new AgentLoopConfig
        {
            Model = "m",
            ConversationId = "c",
            ChannelId = "ch",
        };

        Assert.Equal(0, config.MaxTransientStreamRetries);
    }

    [Fact]
    public void SubagentRunner_OptsIn_BecauseSubagentsDiscardContentDeltas()
    {
        Assert.True(SubagentRunner.DefaultTransientStreamRetries > 0);
    }

    private int StreamCallCount()
        => this.llmClient.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(ILlmClient.StreamCompleteAsync));
    // ── Doubles ────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<LlmStreamChunk> ErrorStream(string error)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ErrorMessage = error };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCallStream(
        string callId, string toolName, string args)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ToolCallDeltas = [new LlmToolCallDelta
            {
                Index = 0, Id = callId, Name = toolName, ArgumentsDelta = args,
            }],
            IsComplete = true,
            FinishReason = "tool_calls",
        };
    }

    /// <summary>A stream that half-streams tool-call arguments, then faults transiently.</summary>
    private static async IAsyncEnumerable<LlmStreamChunk> PartialToolCallThenTransientFault()
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ToolCallDeltas = [new LlmToolCallDelta
            {
                Index = 0, Id = "call_dead", Name = "echo", ArgumentsDelta = """{"text":"trun""",
            }],
        };
        yield return new LlmStreamChunk
        {
            IsComplete = true,
            ErrorMessage = LlmStreamFault.TransientPrefix + "LLM stream produced no data for 300s.",
        };
    }

    private sealed class RecordingTool : IAgentTool
    {
        public List<string> Invocations { get; } = [];

        public string Name => "echo";
        public string Description => "echoes";
        public string ParametersSchema => """{"type":"object","properties":{"text":{"type":"string"}}}""";

        public Task<AgentToolResult> ExecuteAsync(
            string args, ToolExecutionContext ctx, CancellationToken ct)
        {
            this.Invocations.Add(args);
            return Task.FromResult(new AgentToolResult { Success = true, Content = "ok" });
        }
    }
    private sealed class NoopTool : IAgentTool
    {
        public string Name => "noop";
        public string Description => "does nothing";
        public string ParametersSchema => """{"type":"object","properties":{}}""";
        public Task<AgentToolResult> ExecuteAsync(
            string args, ToolExecutionContext ctx, CancellationToken ct)
            => Task.FromResult(new AgentToolResult { Success = true, Content = "ok" });
    }

    private sealed class RecordingCallbacks : IAgentLoopCallbacks
    {
        private readonly List<LlmMessage> messages;

        public RecordingCallbacks(List<LlmMessage> messages) => this.messages = messages;

        public bool ErrorReceived { get; private set; }
        public bool OverflowRecovered { get; private set; }
        public bool RecoverFromOverflow { get; set; }
        public int RoundsCompleted { get; private set; }

        public Task<List<LlmMessage>> PrepareMessagesAsync(int round, CancellationToken ct)
            => Task.FromResult<List<LlmMessage>>([.. this.messages]);
        public void DrainInjectedMessages() { }
        public Task OnContentDeltaAsync(string delta, int seq, CancellationToken ct) => Task.CompletedTask;
        public Task OnToolStartAsync(LlmToolCall tc, CancellationToken ct) => Task.CompletedTask;
        public Task OnToolCompleteAsync(LlmToolCall tc, AgentToolResult r, TimeSpan d, CancellationToken ct)
            => Task.CompletedTask;
        public Task OnRoundCompleteAsync(int round, LlmTokenUsage? usage, CancellationToken ct)
        {
            this.RoundsCompleted++;
            return Task.CompletedTask;
        }
        public Task<bool> OnContextOverflowAsync(string err, CancellationToken ct)
        {
            this.OverflowRecovered = this.RecoverFromOverflow;
            return Task.FromResult(this.RecoverFromOverflow);
        }
        public Task OnErrorAsync(string err, CancellationToken ct)
        {
            this.ErrorReceived = true;
            return Task.CompletedTask;
        }
        public Task OnDoomLoopAsync(string tool, CancellationToken ct) => Task.CompletedTask;
        public Task OnLoopCompleteAsync(AgentLoopResult result, CancellationToken ct) => Task.CompletedTask;
        public void OnAssistantMessage(LlmMessage msg) => this.messages.Add(msg);
        public void OnToolResultMessage(LlmMessage msg) => this.messages.Add(msg);
    }
}
