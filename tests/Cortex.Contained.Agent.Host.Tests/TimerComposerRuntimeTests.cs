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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers how a fired session timer reaches the model, end to end through <see cref="AgentRuntime"/>.
/// <para>
/// A timer used to be appended to the live conversation as <c>Role = "user"</c>, so the model could
/// not structurally tell a timer from the person speaking and the instruction text permanently
/// consumed conversation context. It is now answered by a focused run over a bounded tail, and only
/// what the agent decides to say comes back into the conversation.
/// </para>
/// </summary>
public sealed class TimerComposerRuntimeTests : IAsyncLifetime
{
    private readonly AgentRuntime runtime;
    private readonly AgentSessionStore sessions;
    private readonly AgentMessageChannel messageChannel = new();
    private readonly ILlmClient llmClient = Substitute.For<ILlmClient>();
    private readonly IMessageStore messageStore = Substitute.For<IMessageStore>();

    private const string ComposedReply = "Round 2 — guards up!";

    private readonly List<LlmCompletionRequest> requests = [];

    public TimerComposerRuntimeTests()
    {
        var sessionConfig = new SessionConfig { TimerComposerTailTurns = 4 };
        this.sessions = new AgentSessionStore(
            sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);

        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([new PingTool()], activeChannelStore, NullLogger<ToolRegistry>.Instance);

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        hubClients.Client(Arg.Any<string>()).Returns(Substitute.For<IAgentHubClient>());
        var bridgeAccessor = new BridgeClientAccessor(hubContext);
        bridgeAccessor.SetConnectionId("test-conn");

        var imageAging = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAging.CurrentValue.Returns(new ImageAgingConfig());

        this.llmClient
            .StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (this.requests)
                {
                    this.requests.Add(call.Arg<LlmCompletionRequest>());
                }

                return Reply(ComposedReply);
            });

        this.runtime = new AgentRuntime(
            this.sessions, this.llmClient, toolRegistry, sessionConfig, this.messageChannel,
            bridgeAccessor, activeChannelStore, Substitute.For<IHttpClientFactory>(),
            Path.GetTempPath(), Path.GetTempPath(), NullLogger<AgentRuntime>.Instance,
            new ModelProvider(), imageAging, messageStore: this.messageStore);
    }

    public Task InitializeAsync() => this.runtime.StartProcessingAsync(CancellationToken.None);

    public async Task DisposeAsync() => await this.runtime.StopProcessingAsync(CancellationToken.None);

    private static async IAsyncEnumerable<LlmStreamChunk> Reply(string content)
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

    private AgentSession SeedConversation(params (string Role, string Text)[] turns)
    {
        var session = this.sessions.GetOrCreateWithIdleCheck("conv-timer");
        foreach (var (role, text) in turns)
        {
            session.AddMessage(new LlmMessage { Role = role, Content = text });
        }

        return session;
    }

    private void FireTimer(string intent) =>
        Assert.True(this.messageChannel.TryEnqueue(TimerMessage(intent)));

    private static async Task<LlmMessage> WaitForComposedOutcomeAsync(AgentSession session, int timeoutMs = 10_000)
    {
        // Must wait for a NEW agent message: the seeded conversation may already end on one, so
        // "is there an assistant message?" would return before the timer had been processed at all.
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var assistant = session.GetHistory().LastOrDefault(m => m.Role == "assistant");
            if (assistant?.Content?.Contains(ComposedReply, StringComparison.Ordinal) == true)
            {
                return assistant;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the composed timer outcome never reached the conversation");
        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public async Task The_intent_never_enters_the_conversation_as_something_the_user_said()
    {
        var session = this.SeedConversation(("user", "we're on round 1"), ("assistant", "noted"));

        this.FireTimer("call the next round");
        await WaitForComposedOutcomeAsync(session);

        Assert.DoesNotContain(
            session.GetHistory(),
            m => m.Role == "user" && m.Content?.Contains("call the next round", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task What_the_agent_decides_to_say_becomes_agent_content_in_the_conversation()
    {
        var session = this.SeedConversation(("user", "we're on round 1"), ("assistant", "noted"));

        this.FireTimer("call the next round");
        var assistant = await WaitForComposedOutcomeAsync(session);

        Assert.Contains("Round 2 — guards up!", assistant.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consecutive_agent_messages_are_merged_so_the_provider_never_sees_two_in_a_row()
    {
        // The conversation already ends on an agent turn, so the composed outcome must glue onto
        // it rather than append a second assistant message.
        var session = this.SeedConversation(("user", "we're on round 1"), ("assistant", "noted"));

        this.FireTimer("call the next round");
        await WaitForComposedOutcomeAsync(session);

        var history = session.GetHistory();
        for (var i = 1; i < history.Count; i++)
        {
            Assert.False(
                history[i].Role == "assistant" && history[i - 1].Role == "assistant",
                "two consecutive assistant messages would be rejected by OpenAI/Copilot");
        }
    }

    [Fact]
    public async Task The_composer_run_sees_the_recent_conversation_and_the_intent()
    {
        var session = this.SeedConversation(("user", "we're on round 1"), ("assistant", "noted"));

        this.FireTimer("call the next round");
        await WaitForComposedOutcomeAsync(session);

        var composed = this.SoleRequest();
        Assert.Contains(composed.Messages, m => m.Content?.Contains("we're on round 1", StringComparison.Ordinal) == true);
        Assert.Contains(composed.Messages, m => m.Content?.Contains("call the next round", StringComparison.Ordinal) == true);
        Assert.Equal("system", composed.Messages[0].Role);

        // The framing is what makes the intent structurally distinguishable from the user, and it
        // has to travel in the system prompt — no provider client serialises MessageType.
        Assert.Contains(IntentComposer.Framing, composed.Messages[0].Content, StringComparison.Ordinal);
        Assert.Single(composed.Messages, m => m.Role == "system");
    }

    [Fact]
    public async Task The_composer_run_is_bounded_and_does_not_replay_the_whole_chat()
    {
        // Tail is configured to 4 turns; older exchanges must not be shipped into every timer.
        var turns = new List<(string, string)>();
        for (var i = 0; i < 12; i++)
        {
            turns.Add(("user", $"u{i}"));
            turns.Add(("assistant", $"a{i}"));
        }

        var session = this.SeedConversation([.. turns]);

        this.FireTimer("go");
        await WaitForComposedOutcomeAsync(session);

        var composed = this.SoleRequest();
        Assert.DoesNotContain(composed.Messages, m => m.Content == "u0");
        Assert.Contains(composed.Messages, m => m.Content == "a11");
    }

    [Fact]
    public async Task The_intent_is_the_last_thing_the_model_reads()
    {
        var session = this.SeedConversation(("user", "we're on round 1"), ("assistant", "noted"));

        this.FireTimer("call the next round");
        await WaitForComposedOutcomeAsync(session);

        var composed = this.SoleRequest();
        Assert.Contains("call the next round", composed.Messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timer_that_fires_mid_turn_still_gets_its_own_focused_run()
    {
        // Otherwise the mid-turn drain injects it into the live conversation as a user message —
        // exactly what the composer exists to prevent — just on a narrower path.
        var session = this.sessions.GetOrCreateWithIdleCheck("conv-timer");
        session.AddMessage(new LlmMessage { Role = "user", Content = "we're on round 1" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "noted" });

        this.ScriptToolCallThenAnswer();

        // Seeded directly so the ordering is deterministic: the user turn is drained first and
        // the timer is left for the mid-turn drain. The timer goes through the channel because
        // that is what starts the session loop.
        session.EnqueuePending(UserMessage("what's the score?"));
        this.FireTimer("call the next round");

        // Three calls: the user turn's tool round, its answer, then the timer's own run. Waiting
        // on the reply text alone would be satisfied by the USER turn and prove nothing.
        var composerRequest = await this.WaitForRequestCountAsync(3);

        Assert.DoesNotContain(
            session.GetHistory(),
            m => m.Role == "user" && m.Content?.Contains("call the next round", StringComparison.Ordinal) == true);

        // The third call is the focused run: it ends on the intent and is bounded to the tail.
        Assert.Contains("call the next round", composerRequest.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains(IntentComposer.Framing, composerRequest.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_intent_is_never_written_to_the_stored_message_history()
    {
        // The session history is not the only surface: the MessageStore is what the Bridge and the
        // web UI read back, and a "user" row there would put words in the user's mouth for good.
        var session = this.SeedConversation(("user", "we're on round 1"), ("assistant", "noted"));

        this.FireTimer("call the next round");
        await WaitForComposedOutcomeAsync(session);

        await this.messageStore.DidNotReceive().SaveMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), "user",
            Arg.Is<string>(c => c.Contains("call the next round", StringComparison.Ordinal)),
            Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<Contracts.Hub.MessageCategory>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_run_that_never_reaches_a_clean_answer_leaves_the_conversation_alone()
    {
        // The tool loop appends the model's pre-tool narration ("let me check…") as an assistant
        // message on every round. Taking the last assistant message would glue that fragment into
        // the user's chat whenever the run ends without a final answer — an orphaned half-thought
        // the model then reads back as its own prior utterance.
        var session = this.SeedConversation(("user", "we're on round 1"), ("assistant", "noted"));

        var calls = 0;
        this.llmClient
            .StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (this.requests)
                {
                    this.requests.Add(call.Arg<LlmCompletionRequest>());
                }

                return Interlocked.Increment(ref calls) == 1
                    ? ToolCallWithNarration("Let me check the score first.")
                    : Failure("upstream exploded");
            });

        this.FireTimer("call the next round");
        await this.WaitForRequestCountAsync(2);

        // Give the turn a moment to finish unwinding after the failed call.
        await Task.Delay(250);

        Assert.DoesNotContain(
            session.GetHistory(),
            m => m.Content?.Contains("Let me check the score first.", StringComparison.Ordinal) == true);
        Assert.Equal("noted", session.GetHistory()[^1].Content);
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCallWithNarration(string narration)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ContentDelta = narration };
        yield return new LlmStreamChunk
        {
            ToolCallDeltas =
            [
                new LlmToolCallDelta { Index = 0, Id = "call-1", Name = "ping", ArgumentsDelta = "{}" },
            ],
            IsComplete = true,
            FinishReason = "tool_calls",
        };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> Failure(string error)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ErrorMessage = error, IsComplete = true };
    }

    [Fact]
    public async Task A_barge_in_during_a_timer_run_does_not_rewrite_the_previous_reply()
    {
        // The barge-in path rewrites the conversation's trailing assistant message so the agent
        // remembers only what was actually played. During a composer run that message belongs to
        // an earlier, finished turn — rewriting it destroys an unrelated reply.
        var session = this.SeedConversation(("user", "we're on round 1"), ("assistant", "noted"));

        var started = new TaskCompletionSource();
        this.llmClient
            .StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Hanging(started));

        this.FireTimer("call the next round");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await this.runtime.RecordInterruptedAssistantTurnAsync("conv-timer", "Round 2 — gu…");

        Assert.Equal("noted", session.GetHistory()[^1].Content);
    }

    private static async IAsyncEnumerable<LlmStreamChunk> Hanging(
        TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new LlmStreamChunk { ContentDelta = "thinking" };
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private async Task<LlmCompletionRequest> WaitForRequestCountAsync(int count, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (this.requests)
            {
                if (this.requests.Count >= count)
                {
                    return this.requests[count - 1];
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail($"expected {count} LLM call(s); the focused run never happened");
        throw new InvalidOperationException("unreachable");
    }

    private LlmCompletionRequest SoleRequest()
    {
        lock (this.requests)
        {
            return Assert.Single(this.requests);
        }
    }

    /// <summary>First call asks for a tool (creating a mid-turn drain), the rest answer.</summary>
    private void ScriptToolCallThenAnswer()
    {
        var calls = 0;
        this.llmClient
            .StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (this.requests)
                {
                    this.requests.Add(call.Arg<LlmCompletionRequest>());
                }

                return Interlocked.Increment(ref calls) == 1 ? ToolCall() : Reply(ComposedReply);
            });
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCall()
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ToolCallDeltas =
            [
                new LlmToolCallDelta { Index = 0, Id = "call-1", Name = "ping", ArgumentsDelta = "{}" },
            ],
            IsComplete = true,
            FinishReason = "tool_calls",
        };
    }

    private static AgentMessage UserMessage(string text) => new()
    {
        ConversationId = "conv-timer",
        ChannelId = "webchat-default",
        Text = text,
        Source = AgentMessageSource.User,
        CorrelationId = Guid.NewGuid().ToString("N"),
        Timestamp = DateTimeOffset.UtcNow,
    };

    private static AgentMessage TimerMessage(string intent) => new()
    {
        ConversationId = "conv-timer",
        ChannelId = "webchat-default",
        Text = intent,
        Source = AgentMessageSource.SessionTimer,
        CorrelationId = Guid.NewGuid().ToString("N"),
        Timestamp = DateTimeOffset.UtcNow,
    };

    private sealed class PingTool : IAgentTool
    {
        public string Name => "ping";

        public string Description => "Returns pong.";

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(AgentToolResult.Ok("pong"));
    }
}
