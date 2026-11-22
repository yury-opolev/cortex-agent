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
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Every user-role message reaches the model with the time it was sent, taken from the message's
/// OWN timestamp. A message therefore renders byte-identically on every later request, which keeps
/// the provider's prefix cache warm across turns, tool rounds and mid-turn drains. The stamp is
/// prompt-only: never in session history or the message store.
/// </summary>
public sealed class CurrentTimeStampRuntimeTests : IAsyncLifetime
{
    private const string Winter = "[Sunday 22 November 2026, 16:45 (Europe/Berlin, UTC+01:00)]";
    private const string Winter1650 = "[Sunday 22 November 2026, 16:50 (Europe/Berlin, UTC+01:00)]";
    private const string Summer = "[Thursday 15 July 2027, 14:05 (Europe/Berlin, UTC+02:00)]";

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 15, 45, 0, TimeSpan.Zero));
    private readonly AgentSessionStore sessions;
    private readonly IMessageStore messageStore = Substitute.For<IMessageStore>();
    private readonly List<List<LlmMessage>> requests = [];
    private readonly HookTool tool;
    private AgentMessageChannel messageChannel = new();
    private AgentRuntime runtime = null!;
    private int calls;

    public CurrentTimeStampRuntimeTests()
    {
        this.tool = new HookTool(this.time);
        this.sessions = new AgentSessionStore(
            new SessionConfig { TimerComposerTailTurns = 4 }, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
    }

    public async Task InitializeAsync()
    {
        this.runtime = this.CreateRuntime(enabled: true, toolCallRounds: 0);
        await this.runtime.StartProcessingAsync(CancellationToken.None);
    }

    public async Task DisposeAsync() => await this.runtime.StopProcessingAsync(CancellationToken.None);

    private async Task RestartAsync(bool enabled = true, int toolCallRounds = 0)
    {
        await this.runtime.StopProcessingAsync(CancellationToken.None);
        this.runtime = this.CreateRuntime(enabled, toolCallRounds);
        await this.runtime.StartProcessingAsync(CancellationToken.None);
    }

    private AgentRuntime CreateRuntime(bool enabled, int toolCallRounds)
    {
        this.messageChannel = new AgentMessageChannel();
        var sessionConfig = new SessionConfig { TimerComposerTailTurns = 4 };
        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([this.tool], activeChannelStore, NullLogger<ToolRegistry>.Instance);

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        hubClients.Client(Arg.Any<string>()).Returns(Substitute.For<IAgentHubClient>());
        var bridgeAccessor = new BridgeClientAccessor(hubContext);
        bridgeAccessor.SetConnectionId("test-conn");

        var imageAging = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAging.CurrentValue.Returns(new ImageAgingConfig());

        var llm = Substitute.For<ILlmClient>();
        llm.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (this.requests)
                {
                    this.requests.Add([.. call.Arg<LlmCompletionRequest>().Messages]);
                }

                return Interlocked.Increment(ref this.calls) <= toolCallRounds ? ToolCall() : Reply("done");
            });

        var clock = new OwnerClock(this.time, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), enabled);
        return new AgentRuntime(
            this.sessions, llm, toolRegistry, sessionConfig, this.messageChannel,
            bridgeAccessor, activeChannelStore, Substitute.For<IHttpClientFactory>(),
            Path.GetTempPath(), Path.GetTempPath(), NullLogger<AgentRuntime>.Instance,
            new ModelProvider(), imageAging, messageStore: this.messageStore, ownerClock: clock);
    }

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

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCall()
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ToolCallDeltas = [new LlmToolCallDelta { Index = 0, Id = "call-1", Name = "hook", ArgumentsDelta = "{}" }],
            IsComplete = true,
            FinishReason = "tool_calls",
        };
    }

    private AgentMessage Message(string text, AgentMessageSource source, bool isVoice = false) => new()
    {
        ConversationId = source == AgentMessageSource.ScheduledTask ? "scheduled-task-1" : "conv-1",
        ChannelId = "webchat-default",
        Text = text,
        Source = source,
        IsVoice = isVoice,
        CorrelationId = Guid.NewGuid().ToString("N"),
        Timestamp = this.time.GetUtcNow(),
    };

    private void Send(string text, AgentMessageSource source = AgentMessageSource.User, bool isVoice = false) =>
        Assert.True(this.messageChannel.TryEnqueue(this.Message(text, source, isVoice)));

    private async Task<List<List<LlmMessage>>> WaitForRequestsAsync(int count)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            lock (this.requests)
            {
                if (this.requests.Count >= count)
                {
                    return [.. this.requests];
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail($"expected {count} LLM call(s)");
        throw new InvalidOperationException("unreachable");
    }

    private async Task WaitForAssistantReplyAsync(string conversationId, int replies)
    {
        var session = this.sessions.GetOrCreateWithIdleCheck(conversationId);
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            if (session.GetHistory().Count(m => m is { Role: "assistant", Content: "done" }) >= replies)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("turn never finished");
    }

    private static string Shape(LlmMessage m) =>
        $"{m.Role}|{m.Content}|{string.Join(",", m.ToolCalls?.Select(c => c.Id) ?? [])}|{m.ToolCallId}";

    [Fact]
    public async Task UserTurn_FinalUserMessageCarriesTheTimeItWasSent()
    {
        this.Send("what time is it?");

        var request = Assert.Single(await this.WaitForRequestsAsync(1));

        Assert.Equal(Winter + "\nwhat time is it?", request[^1].Content);
        Assert.Equal("user", request[^1].Role);
    }

    [Fact]
    public async Task UserTurn_SystemPromptExplainsTheStamps()
    {
        this.Send("hi");

        var request = Assert.Single(await this.WaitForRequestsAsync(1));

        Assert.Contains(OwnerClock.SystemPromptNote, request[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserTurn_StampIsNeverPersisted()
    {
        this.Send("what time is it?");
        await this.WaitForAssistantReplyAsync("conv-1", 1);

        var session = this.sessions.GetOrCreateWithIdleCheck("conv-1");
        Assert.Equal("what time is it?", session.GetHistory().First(m => m.Role == "user").Content);
        Assert.All(session.GetHistory(), m => Assert.DoesNotContain("UTC+", m.Content ?? string.Empty, StringComparison.Ordinal));

        var saved = this.messageStore.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMessageStore.SaveMessageAsync))
            .Select(c => c.GetArguments()[3] as string)
            .ToList();
        Assert.Contains("what time is it?", saved);
        Assert.All(saved, content => Assert.DoesNotContain("UTC+", content ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NextTurnAfterToolRounds_StartsWithTheEarlierRequestByteForByte()
    {
        await this.RestartAsync(toolCallRounds: 1);

        this.Send("first");
        await this.WaitForAssistantReplyAsync("conv-1", 1);
        this.time.Advance(TimeSpan.FromHours(3));
        this.Send("second");
        await this.WaitForAssistantReplyAsync("conv-1", 2);

        var requests = await this.WaitForRequestsAsync(3);
        var lastOfTurnOne = requests[1];
        var turnTwo = requests[2];

        Assert.Equal(lastOfTurnOne.Select(Shape), turnTwo.Take(lastOfTurnOne.Count).Select(Shape));
        Assert.Equal(Winter + "\nfirst", turnTwo[1].Content);
        Assert.StartsWith("[Sunday 22 November 2026, 19:5", turnTwo[^1].Content, StringComparison.Ordinal);
        Assert.EndsWith("\nsecond", turnTwo[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageDrainedMidTurn_KeepsEarlierStampsUnchanged_AndGetsItsOwn()
    {
        await this.RestartAsync(toolCallRounds: 1);
        this.tool.OnExecute = () =>
        {
            this.time.Advance(TimeSpan.FromMinutes(5));
            this.sessions.GetOrCreateWithIdleCheck("conv-1").EnqueuePending(this.Message("and one more thing", AgentMessageSource.User));
        };

        this.Send("first");
        await this.WaitForAssistantReplyAsync("conv-1", 1);

        var requests = await this.WaitForRequestsAsync(2);
        Assert.Equal(requests[0].Select(Shape), requests[1].Take(requests[0].Count).Select(Shape));
        Assert.Equal(Winter1650 + "\nand one more thing", requests[1][^1].Content);
        Assert.Equal(Winter + "\nfirst", requests[1][1].Content);
    }

    [Fact]
    public async Task SeededHistory_UsesItsOwnTimestamps_AndAMessageWithoutOneGetsNone()
    {
        await this.runtime.SeedHistoryAsync(
            "conv-1",
            [
                new HubChatMessage { MessageId = "1", Role = "user", Text = "yesterday's question", Timestamp = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero) },
                new HubChatMessage { MessageId = "2", Role = "assistant", Text = "yesterday's answer", Timestamp = new DateTimeOffset(2026, 11, 21, 9, 1, 0, TimeSpan.Zero) },
                new HubChatMessage { MessageId = "3", Role = "user", Text = "undated question" },
                new HubChatMessage { MessageId = "4", Role = "assistant", Text = "undated answer" },
            ],
            CancellationToken.None);

        this.Send("today");

        var request = Assert.Single(await this.WaitForRequestsAsync(1));
        Assert.Equal("[Saturday 21 November 2026, 10:00 (Europe/Berlin, UTC+01:00)]\nyesterday's question", request[1].Content);
        Assert.Equal("undated question", request[3].Content);
        Assert.Equal(Winter + "\ntoday", request[^1].Content);
    }

    [Fact]
    public async Task ScheduledTask_TriggerMessageCarriesTheStamp()
    {
        this.Send("[Scheduled Task] water the plants", AgentMessageSource.ScheduledTask);

        var request = Assert.Single(await this.WaitForRequestsAsync(1));

        Assert.StartsWith(Winter + "\n", request[^1].Content, StringComparison.Ordinal);
        Assert.Contains("water the plants", request[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionTimer_IntentAndTailAreStampedFromTheirOwnTimes()
    {
        var session = this.sessions.GetOrCreateWithIdleCheck("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "remind me later", Timestamp = new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero) });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "ok" });

        this.Send("call the next round", AgentMessageSource.SessionTimer);

        var request = Assert.Single(await this.WaitForRequestsAsync(1));
        Assert.Equal("[Sunday 22 November 2026, 15:00 (Europe/Berlin, UTC+01:00)]\nremind me later", request.First(m => m.Role == "user").Content);
        Assert.Equal(Winter + "\ncall the next round", request[^1].Content);
    }

    [Fact]
    public async Task VoiceTurn_FinalUserMessageCarriesTheStamp()
    {
        this.Send("how long until the meeting?", isVoice: true);

        var request = Assert.Single(await this.WaitForRequestsAsync(1));

        Assert.Equal(Winter + "\nhow long until the meeting?", request[^1].Content);
        Assert.Contains("VOICE", request[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummerTurn_UsesDaylightSavingOffset()
    {
        this.time.SetUtcNow(new DateTimeOffset(2027, 7, 15, 12, 5, 0, TimeSpan.Zero));
        this.Send("hi");

        var request = Assert.Single(await this.WaitForRequestsAsync(1));

        Assert.Equal(Summer + "\nhi", request[^1].Content);
    }

    [Fact]
    public async Task Disabled_NeitherStampNorSystemNoteIsAdded()
    {
        await this.RestartAsync(enabled: false);

        this.Send("hi");

        var request = Assert.Single(await this.WaitForRequestsAsync(1));
        Assert.Equal("hi", request[^1].Content);
        Assert.DoesNotContain("Each user message starts with", request[0].Content, StringComparison.Ordinal);
    }

    private sealed class HookTool(FakeTimeProvider time) : IAgentTool
    {
        public Action? OnExecute { get; set; }

        public string Name => "hook";

        public string Description => "Takes five minutes.";

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (this.OnExecute is { } hook)
            {
                hook();
            }
            else
            {
                time.Advance(TimeSpan.FromMinutes(5));
            }

            return Task.FromResult(AgentToolResult.Ok("ok"));
        }
    }
}
