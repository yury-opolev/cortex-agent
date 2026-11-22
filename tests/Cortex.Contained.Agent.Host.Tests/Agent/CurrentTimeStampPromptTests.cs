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
    private const string Winter = "[Current time: Sunday 22 November 2026, 16:45 (Europe/Berlin, UTC+01:00)]";

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 15, 45, 0, TimeSpan.Zero));

    private OwnerClock NewClock() => new(this.time, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));

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

    [Fact]
    public async Task BuildPrompt_StampsFinalUserMessage_AndLeavesSessionHistoryUntouched()
    {
        using var session = new AgentSession("conv");
        session.AddMessage(new LlmMessage { Role = "user", Content = "hello" });
        var clock = this.NewClock();

        var messages = await NewAssembler(clock).BuildPromptAsync(
            session, CancellationToken.None, turnStamper: new TurnTimeStamper(clock));

        Assert.Equal(Winter + "\nhello", messages[^1].Content);
        Assert.Equal("hello", Assert.Single(session.GetHistory()).Content);
    }

    [Fact]
    public async Task BuildPrompt_WithoutAStamper_AddsNoStamp()
    {
        using var session = new AgentSession("conv");
        session.AddMessage(new LlmMessage { Role = "user", Content = "hello" });

        var messages = await NewAssembler(this.NewClock()).BuildPromptAsync(session, CancellationToken.None);

        Assert.Equal("hello", messages[^1].Content);
    }

    [Fact]
    public async Task BuildPrompt_SystemPromptHasTheNote_ButNeverTheTime()
    {
        using var session = new AgentSession("conv");
        var clock = this.NewClock();

        var system = (await NewAssembler(clock).BuildPromptAsync(session, CancellationToken.None))[0].Content!;

        Assert.Contains(OwnerClock.SystemPromptNote, system, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPrompt_NoClock_SystemPromptIsUnchanged()
    {
        using var session = new AgentSession("conv");

        var system = (await NewAssembler(null).BuildPromptAsync(session, CancellationToken.None))[0].Content!;

        Assert.DoesNotContain("[Current time: ...]", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPrompt_ConsecutiveTurns_ShareAByteIdenticalPrefix()
    {
        using var session = new AgentSession("conv");
        var clock = this.NewClock();
        var assembler = NewAssembler(clock);

        session.AddMessage(new LlmMessage { Role = "user", Content = "one" });
        var turn1 = await assembler.BuildPromptAsync(session, CancellationToken.None, turnStamper: new TurnTimeStamper(clock));
        session.AddMessage(new LlmMessage { Role = "assistant", Content = "answer" });
        this.time.Advance(TimeSpan.FromHours(7));
        session.AddMessage(new LlmMessage { Role = "user", Content = "two" });
        var turn2 = await assembler.BuildPromptAsync(session, CancellationToken.None, turnStamper: new TurnTimeStamper(clock));

        Assert.Equal(turn1[0].Content, turn2[0].Content);
        Assert.Equal(2, turn1.Count); // system + stamped final user message
        Assert.Equal("one", turn2[1].Content);
        Assert.Equal("answer", turn2[2].Content);
        Assert.Contains("23:45", turn2[3].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subagent_FinalUserMessageCarriesTheStamp_AndSystemPromptTheNote()
    {
        var requests = new List<List<LlmMessage>>();
        var llm = ScriptedLlm(requests, rounds: 0);
        var runner = new SubagentRunner(llm, Registry(), 0, NullLogger<SubagentRunner>.Instance, ownerClock: this.NewClock());

        await runner.RunAsync("m", "You are a worker.", "do the thing", "sub-1", CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal(Winter + "\ndo the thing", request[^1].Content);
        Assert.StartsWith("You are a worker.", request[0].Content, StringComparison.Ordinal);
        Assert.Contains(OwnerClock.SystemPromptNote, request[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subagent_StampStaysStableAcrossToolRounds()
    {
        var requests = new List<List<LlmMessage>>();
        var advancing = new AdvancingTool(this.time);
        var llm = ScriptedLlm(requests, rounds: 2);
        var runner = new SubagentRunner(llm, Registry(advancing), 0, NullLogger<SubagentRunner>.Instance, ownerClock: this.NewClock());

        await runner.RunAsync("m", "sys", "do the thing", "sub-1", CancellationToken.None);

        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Equal(Winter + "\ndo the thing", r.First(m => m.Role == "user").Content));
    }

    [Fact]
    public async Task Subagent_ResumedHistory_IsNotModified()
    {
        var requests = new List<List<LlmMessage>>();
        var llm = ScriptedLlm(requests, rounds: 0);
        var runner = new SubagentRunner(llm, Registry(), 0, NullLogger<SubagentRunner>.Instance, ownerClock: this.NewClock());
        List<LlmMessage> stored =
        [
            new() { Role = "system", Content = "old system prompt" },
            new() { Role = "user", Content = "original task" },
            new() { Role = "assistant", Content = "finished" },
            new() { Role = "user", Content = "one more thing" },
        ];

        await runner.ResumeAsync("m", stored, "sub-1", CancellationToken.None);

        var request = Assert.Single(requests);
        Assert.Equal(Winter + "\none more thing", request[^1].Content);
        Assert.Equal("original task", request[1].Content);
        Assert.Equal("one more thing", stored[3].Content);
        Assert.Equal("old system prompt", stored[0].Content);
        Assert.Contains(OwnerClock.SystemPromptNote, request[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subagent_WithoutAClock_SendsPromptsAsBefore()
    {
        var requests = new List<List<LlmMessage>>();
        var llm = ScriptedLlm(requests, rounds: 0);
        var runner = new SubagentRunner(llm, Registry(), 0, NullLogger<SubagentRunner>.Instance);

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
            ToolCallDeltas = [new LlmToolCallDelta { Index = 0, Id = $"call-{n}", Name = "advance", ArgumentsDelta = $$"""{"n":{{n}}}""" }],
            IsComplete = true,
            FinishReason = "tool_calls",
        };
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
