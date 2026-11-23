using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class LabellingLlmTests
{
    private readonly FakeLabellingLlm fake = new();
    private readonly List<TimeSpan> waits = [];

    private LabellingLlm Create(LabellingOptions? options = null, int callsSoFar = 0) =>
        new(this.fake, FakeLabellingLlm.ServesAll, options ?? new LabellingOptions { MinCallIntervalMs = 0 }, callsSoFar, (wait, _) =>
        {
            this.waits.Add(wait);
            return Task.CompletedTask;
        });

    private sealed record Answer(string? Text);

    [Fact]
    public async Task CompleteJsonAsync_FencedJson_Parsed()
    {
        this.fake.Respond = (_, _, _) => "```json\n{\"text\":\"zqanswer\"}\n```";

        var answer = await this.Create().CompleteJsonAsync<Answer>("gpt-6.1-sol", "s", "u", 100, a => a.Text is not null, CancellationToken.None);

        Assert.Equal("zqanswer", answer!.Text);
        Assert.Equal(("gpt-6.1-sol", "s", "u"), Assert.Single(this.fake.Requests));
    }

    [Fact]
    public async Task CompleteJsonAsync_InvalidThenValid_AsksAgainWithoutBackoff()
    {
        var replies = new Queue<string>(["not json", "{\"text\":null}", "{\"text\":\"ok\"}"]);
        this.fake.Respond = (_, _, _) => replies.Dequeue();
        var llm = this.Create();

        var answer = await llm.CompleteJsonAsync<Answer>("m", "s", "u", 100, a => a.Text is not null, CancellationToken.None);

        Assert.Equal("ok", answer!.Text);
        Assert.Equal(3, llm.Calls);
        Assert.Empty(this.waits);
    }

    [Fact]
    public async Task CompleteJsonAsync_FailuresAndFailover_BackOffThenGiveUp()
    {
        this.fake.Respond = (_, _, _) => "{\"text\":\"from another family\"}";
        this.fake.AnsweringProvider = "other-provider";

        await Assert.ThrowsAsync<LabellingTransportException>(() => this.Create(new LabellingOptions { MinCallIntervalMs = 0, MaxAttempts = 3 })
            .CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None));

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], this.waits);
    }

    [Fact]
    public async Task CompleteJsonAsync_EveryCall_IsPinnedToTheProviderThatListsTheModel()
    {
        this.fake.Respond = (_, _, _) => "{\"text\":\"x\"}";

        await this.Create().CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None);

        Assert.True(Assert.Single(this.fake.Sent).PinnedProvider);
    }

    [Fact]
    public async Task CompleteJsonAsync_EveryAttemptFailsInTransport_ThrowsInsteadOfReturningNoAnswer()
    {
        this.fake.Respond = (_, _, _) => null;
        var llm = this.Create(new LabellingOptions { MinCallIntervalMs = 0, MaxAttempts = 3 });

        var error = await Assert.ThrowsAsync<LabellingTransportException>(() =>
            llm.CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None));

        Assert.Equal(3, llm.Calls);
        Assert.Contains("HTTP 503", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteJsonAsync_OnlyInvalidAnswers_ReturnsNull()
    {
        this.fake.Respond = (_, _, _) => "{\"text\":null}";

        var answer = await this.Create(new LabellingOptions { MinCallIntervalMs = 0, MaxAttempts = 3 })
            .CompleteJsonAsync<Answer>("m", "s", "u", 100, a => a.Text is not null, CancellationToken.None);

        Assert.Null(answer);
    }

    [Fact]
    public void Backoff_RateLimited_WaitsLongerAndIsCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), LabellingLlm.Backoff(0, "HTTP 429: slow down"));
        Assert.Equal(TimeSpan.FromSeconds(120), LabellingLlm.Backoff(9, null));
    }

    [Fact]
    public async Task CompleteJsonAsync_AtTheCap_ThrowsWithoutCalling()
    {
        this.fake.Respond = (_, _, _) => "{\"text\":\"x\"}";
        var llm = this.Create(new LabellingOptions { MaxCalls = 5, MinCallIntervalMs = 0 }, callsSoFar: 5);

        await Assert.ThrowsAsync<LabellingBudgetExceededException>(() =>
            llm.CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None));
        Assert.Empty(this.fake.Requests);
    }

    [Fact]
    public async Task CompleteJsonAsync_TwoCalls_SecondWaitsForTheInterval()
    {
        this.fake.Respond = (_, _, _) => "{\"text\":\"x\"}";
        var llm = this.Create(new LabellingOptions { MinCallIntervalMs = 60_000 });

        await llm.CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None);
        await llm.CompleteJsonAsync<Answer>("m", "s", "u", 100, _ => true, CancellationToken.None);

        var wait = Assert.Single(this.waits);
        Assert.InRange(wait, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void EnsureServed_UnlistedModel_NamesIt()
    {
        var llm = new LabellingLlm(this.fake, model => model == "gpt-6.1-sol" ? "copilot" : null, new LabellingOptions());

        var error = Assert.Throws<InvalidOperationException>(() => llm.EnsureServed("gpt-6.1-sol", "claude-opus-5.5"));

        Assert.Contains("claude-opus-5.5", error.Message, StringComparison.Ordinal);
    }
}
