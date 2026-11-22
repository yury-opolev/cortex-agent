using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class CurrentTimeStampPromptTests
{
    private const string At1645 = "[Sunday 22 November 2026, 16:45 (Europe/Berlin, UTC+01:00)]";
    private const string At1650 = "[Sunday 22 November 2026, 16:50 (Europe/Berlin, UTC+01:00)]";

    private static readonly DateTimeOffset T1645 = new(2026, 11, 22, 15, 45, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1650 = new(2026, 11, 22, 15, 50, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider time = new(T1650);

    private OwnerClock NewClock(bool enabled = true) =>
        new(this.time, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), enabled);

    private static PromptAssembler NewAssembler(OwnerClock? clock)
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.ContextWindow.Returns(128_000);
        modelProvider.DefaultModel.Returns("test-model");
        var imageAging = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAging.CurrentValue.Returns(new ImageAgingConfig());

        return new PromptAssembler(
            () => "You are a test.",
            modelProvider,
            imageAging,
            NullLogger<PromptAssembler>.Instance,
            ownerClock: clock);
    }

    private static string?[] Contents(IEnumerable<LlmMessage> messages) => [.. messages.Select(m => m.Content)];

    [Fact]
    public async Task BuildPrompt_StampsEveryUserMessage_AndLeavesSessionHistoryUntouched()
    {
        using var session = new AgentSession("conv");
        session.AddMessage(new LlmMessage { Role = "user", Content = "one", Timestamp = T1645 });
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "answer" });
        session.AddMessage(new LlmMessage { Role = "user", Content = "two", Timestamp = T1650 });

        var messages = await NewAssembler(this.NewClock()).BuildPromptAsync(session, CancellationToken.None);

        Assert.Equal(At1645 + "\none", messages[1].Content);
        Assert.Equal("answer", messages[2].Content);
        Assert.Equal(At1650 + "\ntwo", messages[3].Content);
        Assert.Equal("one", session.GetHistory()[0].Content);
        Assert.Equal("answer", session.GetHistory()[1].Content);
        Assert.Equal("two", session.GetHistory()[2].Content);
    }

    [Fact]
    public async Task BuildPrompt_SeededMessageWithoutATimestamp_IsNotStamped()
    {
        using var session = new AgentSession("conv");
        session.AddMessage(new LlmMessage { Role = "user", Content = "seeded", Timestamp = default });

        var messages = await NewAssembler(this.NewClock()).BuildPromptAsync(session, CancellationToken.None);

        Assert.Equal("seeded", messages[^1].Content);
    }

    [Fact]
    public async Task BuildPrompt_SwitchOff_StampsNothingAndAddsNoNote()
    {
        using var session = new AgentSession("conv");
        session.AddMessage(new LlmMessage { Role = "user", Content = "one", Timestamp = T1645 });

        var messages = await NewAssembler(this.NewClock(enabled: false)).BuildPromptAsync(session, CancellationToken.None);

        Assert.Equal("one", messages[^1].Content);
        Assert.DoesNotContain("Each user message starts with", messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPrompt_NoClock_BehavesAsBefore()
    {
        using var session = new AgentSession("conv");
        session.AddMessage(new LlmMessage { Role = "user", Content = "one", Timestamp = T1645 });

        var messages = await NewAssembler(null).BuildPromptAsync(session, CancellationToken.None);

        Assert.Equal("one", messages[^1].Content);
        Assert.DoesNotContain("Each user message starts with", messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPrompt_SystemPromptHasTheStaticNote_ThatDoesNotChangeWithTheTime()
    {
        using var session = new AgentSession("conv");
        var assembler = NewAssembler(this.NewClock());

        var before = (await assembler.BuildPromptAsync(session, CancellationToken.None))[0].Content!;
        this.time.Advance(TimeSpan.FromDays(40));
        var after = (await assembler.BuildPromptAsync(session, CancellationToken.None))[0].Content!;

        Assert.Contains(OwnerClock.SystemPromptNote, before, StringComparison.Ordinal);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task BuildPrompt_NextTurnAfterToolRounds_StartsWithTheEntireEarlierRequestUnchanged()
    {
        using var session = new AgentSession("conv");
        var assembler = NewAssembler(this.NewClock());

        // Turn N: user message and a tool round; the final answer is not part of this request yet.
        session.AddMessage(new LlmMessage { Role = "user", Content = "one", Timestamp = T1645 });
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            ToolCalls = [new LlmToolCall { Id = "c1", Name = "t", Arguments = "{}" }],
        });
        session.AddMessage(new LlmMessage { Role = "tool", Content = "result", ToolCallId = "c1" });
        var requestN = await assembler.BuildPromptAsync(session, CancellationToken.None);

        // Hours later: the answer and the next user message arrive.
        this.time.Advance(TimeSpan.FromHours(7));
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "answer" });
        session.AddMessage(new LlmMessage { Role = "user", Content = "two", Timestamp = T1650.AddHours(7) });
        var requestNext = await assembler.BuildPromptAsync(session, CancellationToken.None);

        Assert.Equal(Contents(requestN), Contents(requestNext.Take(requestN.Count)));
        Assert.Equal(At1645 + "\none", requestNext[1].Content);
        Assert.Contains("23:50", requestNext[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subagent_StampsTheTaskPromptAndInjectedMessages_AndKeepsEarlierStampsStable()
    {
        var requests = new List<List<LlmMessage>>();
        var injector = new CallbackTool(this.time, T1650.AddMinutes(5));
        var llm = ScriptedLlm(requests, rounds: 2);
        var runner = new SubagentRunner(llm, Registry(injector), 0, NullLogger<SubagentRunner>.Instance, ownerClock: this.NewClock());
        injector.Runner = runner;

        await runner.RunAsync("m", "You are a worker.", "do the thing", "sub-1", CancellationToken.None);

        Assert.Equal(3, requests.Count);
        Assert.Equal(At1650 + "\ndo the thing", requests[0][^1].Content);
        Assert.StartsWith("You are a worker.", requests[0][0].Content, StringComparison.Ordinal);
        Assert.Contains(OwnerClock.SystemPromptNote, requests[0][0].Content, StringComparison.Ordinal);

        // Round 2 carries round 1 untouched; the message injected meanwhile is stamped from its own time.
        Assert.Equal(Contents(requests[0]), Contents(requests[1].Take(requests[0].Count)));
        var injectedMessage = requests[2].Last(m => m.Role == "user");
        Assert.Equal("[Sunday 22 November 2026, 16:55 (Europe/Berlin, UTC+01:00)]\nwhile you were working", injectedMessage.Content);
        Assert.Equal(Contents(requests[1]), Contents(requests[2].Take(requests[1].Count)));
    }

    [Fact]
    public async Task Subagent_ResumedHistory_IsStampedFromStoredTimestamps_AndNotModified()
    {
        var requests = new List<List<LlmMessage>>();
        var runner = new SubagentRunner(ScriptedLlm(requests, rounds: 0), Registry(), 0, NullLogger<SubagentRunner>.Instance, ownerClock: this.NewClock());
        List<LlmMessage> stored =
        [
            new() { Role = "system", Content = "old system prompt" },
            new() { Role = "user", Content = "original task", Timestamp = T1645 },
            new() { Role = "assistant", Content = "finished" },
            new() { Role = "user", Content = "one more thing", Timestamp = T1650 },
        ];

        await runner.ResumeAsync("m", stored, "sub-1", CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal(At1645 + "\noriginal task", request[1].Content);
        Assert.Equal(At1650 + "\none more thing", request[3].Content);
        Assert.Equal("original task", stored[1].Content);
        Assert.Equal("old system prompt", stored[0].Content);
        Assert.Contains(OwnerClock.SystemPromptNote, request[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subagent_WithoutAClock_SendsPromptsAsBefore()
    {
        var requests = new List<List<LlmMessage>>();
        var runner = new SubagentRunner(ScriptedLlm(requests, rounds: 0), Registry(), 0, NullLogger<SubagentRunner>.Instance);

        await runner.RunAsync("m", "sys", "do the thing", "sub-1", CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal("do the thing", request[^1].Content);
        Assert.Equal("sys", request[0].Content);
    }

    [Fact]
    public async Task Subagent_SwitchOff_StampsNothing()
    {
        var requests = new List<List<LlmMessage>>();
        var runner = new SubagentRunner(
            ScriptedLlm(requests, rounds: 0), Registry(), 0, NullLogger<SubagentRunner>.Instance, ownerClock: this.NewClock(enabled: false));

        await runner.RunAsync("m", "sys", "do the thing", "sub-1", CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal("do the thing", request[^1].Content);
        Assert.Equal("sys", request[0].Content);
    }

    private static ToolRegistry Registry(params IAgentTool[] tools) =>
        new(tools, new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

    private static ILlmClient ScriptedLlm(List<List<LlmMessage>> requests, int rounds)
    {
        var llm = Substitute.For<ILlmClient>();
        var calls = 0;
        llm.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requests.Add([.. call.Arg<LlmCompletionRequest>().Messages]);
                return Interlocked.Increment(ref calls) <= rounds ? ToolCall(calls) : Reply("finished");
            });
        return llm;
    }

    private static async IAsyncEnumerable<LlmStreamChunk> Reply(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ContentDelta = content, IsComplete = true, FinishReason = "stop" };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCall(int n)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ToolCallDeltas = [new LlmToolCallDelta { Index = 0, Id = $"call-{n}", Name = "work", ArgumentsDelta = $$"""{"n":{{n}}}""" }],
            IsComplete = true,
            FinishReason = "tool_calls",
        };
    }

    /// <summary>On its first call, moves the clock and injects a follow-up into the running subagent.</summary>
    private sealed class CallbackTool(FakeTimeProvider time, DateTimeOffset injectedAt) : IAgentTool
    {
        private int calls;

        public SubagentRunner? Runner { get; set; }

        public string Name => "work";

        public string Description => "Does some work.";

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref this.calls) == 1)
            {
                time.SetUtcNow(injectedAt);
                Assert.True(this.Runner!.InjectMessage("while you were working"));
            }

            return Task.FromResult(AgentToolResult.Ok("ok"));
        }
    }
}
