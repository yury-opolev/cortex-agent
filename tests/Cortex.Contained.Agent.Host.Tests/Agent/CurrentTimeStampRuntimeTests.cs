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
/// The current local date and time reaches the model on every turn as a transient stamp on the
/// FINAL user message of the prompt. Nothing in the (cached) prefix changes, and the stamp is never
/// persisted: not in session history, not in the message store.
/// </summary>
public sealed class CurrentTimeStampRuntimeTests : IAsyncLifetime
{
    private const string Winter = "[Current time: Sunday 22 November 2026, 16:45 (Europe/Berlin, UTC+01:00)]";
    private const string WinterPlus5 = "[Current time: Sunday 22 November 2026, 16:50 (Europe/Berlin, UTC+01:00)]";
    private const string Summer = "[Current time: Thursday 15 July 2027, 14:05 (Europe/Berlin, UTC+02:00)]";

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 15, 45, 0, TimeSpan.Zero));
    private readonly AgentSessionStore sessions;
    private AgentMessageChannel messageChannel = new();
    private readonly IMessageStore messageStore = Substitute.For<IMessageStore>();
    private readonly List<List<LlmMessage>> requests = [];
    private readonly AdvancingTool tool;
    private AgentRuntime runtime = null!;
    private int calls;

    public CurrentTimeStampRuntimeTests()
    {
        this.tool = new AdvancingTool(this.time);
        var sessionConfig = new SessionConfig { TimerComposerTailTurns = 4 };
        this.sessions = new AgentSessionStore(
            sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
    }

    public async Task InitializeAsync()
    {
        this.runtime = this.CreateRuntime(enabled: true, toolCallRounds: 0);
        await this.runtime.StartProcessingAsync(CancellationToken.None);
    }

    public async Task DisposeAsync() => await this.runtime.StopProcessingAsync(CancellationToken.None);

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
            ToolCallDeltas = [new LlmToolCallDelta { Index = 0, Id = "call-1", Name = "advance", ArgumentsDelta = "{}" }],
            IsComplete = true,
            FinishReason = "tool_calls",
        };
    }

    private static AgentMessage Message(string text, AgentMessageSource source, bool isVoice = false) => new()
    {
        ConversationId = source == AgentMessageSource.ScheduledTask ? "scheduled-task-1" : "conv-1",
        ChannelId = "webchat-default",
        Text = text,
        Source = source,
        IsVoice = isVoice,
        CorrelationId = Guid.NewGuid().ToString("N"),
        Timestamp = DateTimeOffset.UtcNow,
    };

    private void Send(string text, AgentMessageSource source = AgentMessageSource.User, bool isVoice = false) =>
        Assert.True(this.messageChannel.TryEnqueue(Message(text, source, isVoice)));

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

    [Fact]
    public async Task UserTurn_FinalUserMessageCarriesTheStamp()
    {
        this.Send("what time is it?");

        var request = Assert.Single(await this.WaitForRequestsAsync(1));

        Assert.Equal(Winter + "\nwhat time is it?", request[^1].Content);
        Assert.Equal("user", request[^1].Role);
    }

    [Fact]
    public async Task UserTurn_SystemPromptExplainsTheStamp()
    {
        this.Send("hi");

        var request = Assert.Single(await this.WaitForRequestsAsync(1));

        Assert.Contains("[Current time: ...]", request[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", request[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserTurn_StampIsNeverPersisted()
    {
        this.Send("what time is it?");
        await this.WaitForAssistantReplyAsync("conv-1", 1);

        var session = this.sessions.GetOrCreateWithIdleCheck("conv-1");
        Assert.Equal("what time is it?", session.GetHistory().First(m => m.Role == "user").Content);
        Assert.All(session.GetHistory(), m => Assert.DoesNotContain("Current time", m.Content ?? string.Empty, StringComparison.Ordinal));

        var saved = this.messageStore.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMessageStore.SaveMessageAsync))
            .Select(c => c.GetArguments()[3] as string)
            .ToList();
        Assert.Contains("what time is it?", saved);
        Assert.All(saved, content => Assert.DoesNotContain("Current time", content ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondTurn_EarlierMessagesAreRenderedWithoutTheirStamp_SoThePrefixStaysStable()
    {
        this.Send("first");
        await this.WaitForAssistantReplyAsync("conv-1", 1);
        this.time.Advance(TimeSpan.FromMinutes(5));
        this.Send("second");
        await this.WaitForAssistantReplyAsync("conv-1", 2);

        var requests = await this.WaitForRequestsAsync(2);
        var first = requests[0];
        var second = requests[1];

        // Everything the first request sent except its stamped final message is a byte-identical
        // prefix of the second request; the old message is back to its raw text.
        Assert.Equal(first[0].Content, second[0].Content);
        Assert.Equal("first", second[1].Content);
        Assert.Equal("done", second[2].Content);
        Assert.Equal(WinterPlus5 + "\nsecond", second[^1].Content);
        Assert.Equal(1, second.Count(m => m.Content?.Contains("Current time", StringComparison.Ordinal) == true && m.Role == "user"));
    }

    [Fact]
    public async Task ToolRounds_KeepOneStampForTheWholeTurn()
    {
        await this.runtime.StopProcessingAsync(CancellationToken.None);
        this.runtime = this.CreateRuntime(enabled: true, toolCallRounds: 2);
        await this.runtime.StartProcessingAsync(CancellationToken.None);

        this.Send("work");
        await this.WaitForAssistantReplyAsync("conv-1", 1);

        var requests = await this.WaitForRequestsAsync(3);
        Assert.All(requests, r => Assert.Equal(Winter + "\nwork", r.First(m => m.Role == "user").Content));
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T15:55:00Z", null), this.time.GetUtcNow());
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
    public async Task SessionTimer_IntentCarriesTheStamp()
    {
        var session = this.sessions.GetOrCreateWithIdleCheck("conv-1");
        session.AddMessage(new LlmMessage { Role = "user", Content = "remind me later" });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "ok" });

        this.Send("call the next round", AgentMessageSource.SessionTimer);

        var request = Assert.Single(await this.WaitForRequestsAsync(1));
        Assert.Equal(Winter + "\ncall the next round", request[^1].Content);
        Assert.Equal("remind me later", request.First(m => m.Role == "user").Content);
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
        await this.runtime.StopProcessingAsync(CancellationToken.None);
        this.runtime = this.CreateRuntime(enabled: false, toolCallRounds: 0);
        await this.runtime.StartProcessingAsync(CancellationToken.None);

        this.Send("hi");

        var request = Assert.Single(await this.WaitForRequestsAsync(1));
        Assert.Equal("hi", request[^1].Content);
        Assert.DoesNotContain("[Current time: ...]", request[0].Content, StringComparison.Ordinal);
    }

    private sealed class AdvancingTool(FakeTimeProvider time) : IAgentTool
    {
        public string Name => "advance";

        public string Description => "Takes five minutes.";

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            time.Advance(TimeSpan.FromMinutes(5));
            return Task.FromResult(AgentToolResult.Ok("ok"));
        }
    }
}
