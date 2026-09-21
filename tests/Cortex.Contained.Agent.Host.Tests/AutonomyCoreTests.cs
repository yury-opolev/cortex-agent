using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Agent.Autonomy;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class AutonomyCoreTests
{
    [Fact]
    public void Continue_CreatedWithRemainingText_CarriesRemainingText()
    {
        var verdict = GoalVerdict.Continue("write the missing tests");

        var continuation = Assert.IsType<GoalVerdict.ContinueVerdict>(verdict);
        Assert.Equal("write the missing tests", continuation.Remaining);
    }

    [Fact]
    public void Stop_CreatedWithNoneOutcome_Throws()
    {
        Assert.Throws<ArgumentException>(() => GoalVerdict.Stop(GoalOutcome.None, "not terminal"));
    }

    [Fact]
    public void CreateDefault_NewBudget_UsesBackstopLimits()
    {
        var budget = GoalBudget.Create(new FakeTimeProvider());

        Assert.Equal(TimeSpan.FromDays(7), budget.WallClockLimit);
        Assert.Equal(10_000, budget.ContinuationLimit);
    }

    [Fact]
    public void Rehydrate_PreviouslyConsumedValues_RestoresConsumedState()
    {
        var consumed = new GoalBudgetConsumed(TimeSpan.FromHours(3), 42);

        var budget = GoalBudget.Rehydrate(consumed, new FakeTimeProvider());

        Assert.Equal(consumed, budget.Consumed);
    }

    [Fact]
    public void Consumed_InjectedClockAdvances_ReportsElapsedWithoutSleeping()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var budget = GoalBudget.Create(clock);

        clock.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(TimeSpan.FromMinutes(90), budget.Consumed.Elapsed);
    }

    [Fact]
    public void RecordContinuation_Called_IncrementsConsumedCount()
    {
        var budget = GoalBudget.Create(new FakeTimeProvider());

        budget.RecordContinuation();
        budget.RecordContinuation();

        Assert.Equal(2, budget.Consumed.ContinuationsUsed);
    }

    [Fact]
    public void Remaining_WithFiniteLimits_ReportsBothDimensions()
    {
        var clock = new FakeTimeProvider();
        var budget = GoalBudget.Rehydrate(
            new GoalBudgetConsumed(TimeSpan.FromDays(2), 3),
            clock);

        clock.Advance(TimeSpan.FromHours(12));

        Assert.Equal(TimeSpan.FromDays(4.5), budget.WallClockRemaining);
        Assert.Equal(9_997, budget.ContinuationsRemaining);
    }

    [Fact]
    public void Create_WithParsedNoLimitValues_DisablesBothLimits()
    {
        var budget = GoalBudget.Create(
            new FakeTimeProvider(),
            GoalBudget.ParseWallClockLimit("none"),
            GoalBudget.ParseContinuationLimit("none"));

        Assert.Null(budget.WallClockLimit);
        Assert.Null(budget.ContinuationLimit);
        Assert.Null(budget.WallClockRemaining);
        Assert.Null(budget.ContinuationsRemaining);
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1_800)]
    [InlineData("2h", 7_200)]
    [InlineData("7d", 604_800)]
    public void ParseWallClockLimit_HumanDuration_ReturnsExpectedTimeSpan(
        string text,
        int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), GoalBudget.ParseWallClockLimit(text));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unlimited")]
    [InlineData("off")]
    public void ParseWallClockLimit_NoLimitWord_ReturnsNoLimit(string text)
    {
        Assert.Null(GoalBudget.ParseWallClockLimit(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two hours")]
    [InlineData("2w")]
    [InlineData("-1h")]
    public void ParseWallClockLimit_InvalidText_ThrowsFormatException(string text)
    {
        Assert.Throws<FormatException>(() => GoalBudget.ParseWallClockLimit(text));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unlimited")]
    [InlineData("off")]
    public void ParseContinuationLimit_NoLimitWord_ReturnsNoLimit(string text)
    {
        Assert.Null(GoalBudget.ParseContinuationLimit(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ten")]
    [InlineData("-1")]
    public void ParseContinuationLimit_InvalidText_ThrowsFormatException(string text)
    {
        Assert.Throws<FormatException>(() => GoalBudget.ParseContinuationLimit(text));
    }

    [Fact]
    public void ParseReply_DoneLine_ReturnsMetOutcome()
    {
        var verdict = CompletionJudge.ParseReply("DONE");

        var stop = Assert.IsType<GoalVerdict.StopVerdict>(verdict);
        Assert.Equal(GoalOutcome.Met, stop.Outcome);
    }

    [Theory]
    [InlineData("  done  ")]
    [InlineData("```text\r\nDONE\r\n```")]
    [InlineData("Here is my verdict:\nDONE")]
    public void ParseReply_ToleratedDoneShapes_ReturnsMetOutcome(string reply)
    {
        var verdict = CompletionJudge.ParseReply(reply);

        var stop = Assert.IsType<GoalVerdict.StopVerdict>(verdict);
        Assert.Equal(GoalOutcome.Met, stop.Outcome);
    }

    [Fact]
    public void ParseReply_ContinueWithColon_ExtractsRemainingText()
    {
        var verdict = CompletionJudge.ParseReply("CONTINUE: add the missing persistence tests");

        var continuation = Assert.IsType<GoalVerdict.ContinueVerdict>(verdict);
        Assert.Equal("add the missing persistence tests", continuation.Remaining);
    }

    [Theory]
    [InlineData("")]
    [InlineData("DONE: probably")]
    [InlineData("CONTINUE add tests")]
    [InlineData("The goal is done.")]
    [InlineData("DONE\nCONTINUE: also add tests")]
    public void ParseReply_AmbiguousOrInvalidReply_FailsOpenToContinue(string reply)
    {
        Assert.IsType<GoalVerdict.ContinueVerdict>(CompletionJudge.ParseReply(reply));
    }

    [Fact]
    public void BuildMessages_GoalAndTranscript_IncludesBothInJudgePrompt()
    {
        var messages = CompletionJudge.BuildMessages(
            "ship autonomy",
            [new LlmMessage { Role = "assistant", Content = "I wrote budget tests." }]);

        Assert.Contains(messages, m => m.Content?.Contains("ship autonomy", StringComparison.Ordinal) == true);
        Assert.Contains(messages, m => m.Content?.Contains("I wrote budget tests.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EvaluateAsync_LlmThrows_FailsOpenToContinue()
    {
        var judge = CreateJudge(llm =>
            llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<LlmCompletionResult>(new InvalidOperationException("boom"))));

        var verdict = await judge.EvaluateAsync("goal", [], CancellationToken.None);

        Assert.IsType<GoalVerdict.ContinueVerdict>(verdict);
    }

    [Fact]
    public async Task EvaluateAsync_EmptyReply_FailsOpenToContinue()
    {
        var judge = CreateJudge(llm =>
            llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
                .Returns(new LlmCompletionResult { Success = true, Content = " " }));

        var verdict = await judge.EvaluateAsync("goal", [], CancellationToken.None);

        Assert.IsType<GoalVerdict.ContinueVerdict>(verdict);
    }

    [Fact]
    public async Task EvaluateAsync_UnparseableReply_FailsOpenToContinue()
    {
        var judge = CreateJudge(llm =>
            llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
                .Returns(new LlmCompletionResult { Success = true, Content = "looks complete to me" }));

        var verdict = await judge.EvaluateAsync("goal", [], CancellationToken.None);

        Assert.IsType<GoalVerdict.ContinueVerdict>(verdict);
    }

    [Fact]
    public async Task EvaluateAsync_TimeoutException_FailsOpenToContinue()
    {
        var judge = CreateJudge(llm =>
            llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<LlmCompletionResult>(new TimeoutException("slow"))));

        var verdict = await judge.EvaluateAsync("goal", [], CancellationToken.None);

        Assert.IsType<GoalVerdict.ContinueVerdict>(verdict);
    }

    [Fact]
    public async Task EvaluateAsync_DoneReply_ReturnsMetOutcome()
    {
        var judge = CreateJudge(llm =>
            llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
                .Returns(new LlmCompletionResult { Success = true, Content = "DONE" }));

        var verdict = await judge.EvaluateAsync("goal", [], CancellationToken.None);

        var stop = Assert.IsType<GoalVerdict.StopVerdict>(verdict);
        Assert.Equal(GoalOutcome.Met, stop.Outcome);
    }

    [Fact]
    public async Task EvaluateAsync_CompletionRequest_UsesDefaultModelFromProvider()
    {
        LlmCompletionRequest? captured = null;
        var judge = CreateJudge(llm =>
            llm.CompleteAsync(
                    Arg.Do<LlmCompletionRequest>(request => captured = request),
                    Arg.Any<CancellationToken>())
                .Returns(new LlmCompletionResult { Success = true, Content = "DONE" }));

        await judge.EvaluateAsync("goal", [], CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("same-run-model", captured.Model);
    }

    private static CompletionJudge CreateJudge(Action<ILlmClient> configure)
    {
        var llm = Substitute.For<ILlmClient>();
        configure(llm);

        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.DefaultModel.Returns("same-run-model");
        modelProvider.MaxOutputTokens.Returns(1234);

        return new CompletionJudge(
            llm,
            modelProvider,
            NullLogger<CompletionJudge>.Instance);
    }
}
