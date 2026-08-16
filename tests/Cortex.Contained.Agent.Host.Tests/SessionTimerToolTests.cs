using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Reminders;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers what the AGENT actually sees from <c>session_timer</c>.
/// <para>
/// The rendered text is the whole deliverable of the timer state machine: a model that cannot tell
/// "this timer never existed" from "this timer already fired and you have its intent" will draw the
/// wrong conclusion no matter how correct the service beneath it is.
/// </para>
/// </summary>
public sealed class SessionTimerToolTests : IDisposable
{
    private readonly AgentMessageChannel queue = new();
    private readonly FakeTimeProvider time = new(DateTimeOffset.Parse("2026-08-15T10:00:00Z", null));
    private readonly SessionTimerService service;
    private readonly SessionTimerTool tool;

    public SessionTimerToolTests()
    {
        this.service = new SessionTimerService(this.queue, NullLogger<SessionTimerService>.Instance, this.time);
        this.tool = new SessionTimerTool(this.service);
    }

    public void Dispose() => this.service.Dispose();

    private static ToolExecutionContext Context(string conversationId = "conv") =>
        new() { ConversationId = conversationId, ChannelId = "ch" };

    private async Task<AgentToolResult> RunAsync(string argumentsJson, string conversationId = "conv") =>
        await this.tool.ExecuteAsync(argumentsJson, Context(conversationId), CancellationToken.None);

    // ── create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_timer_reports_the_id_and_how_to_cancel_it()
    {
        var result = await this.RunAsync("""{"action":"create","delay_seconds":30,"intent":"check in"}""");

        Assert.True(result.Success);
        var id = Assert.Single(this.service.List("conv")).Id;
        Assert.Contains(id, result.Content, StringComparison.Ordinal);
        Assert.Contains("cancel", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creating_a_timer_beyond_the_cap_explains_the_limit_rather_than_throwing()
    {
        for (var i = 0; i < SessionTimerService.PerConversationCap; i++)
        {
            Assert.True((await this.RunAsync("""{"action":"create","delay_seconds":600,"intent":"x"}""")).Success);
        }

        var result = await this.RunAsync("""{"action":"create","delay_seconds":600,"intent":"one too many"}""");

        Assert.False(result.Success);
        Assert.Contains("cap", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SessionTimerService.PerConversationCap, this.service.ActiveCount("conv"));
    }

    // ── list ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Listing_nothing_says_so_plainly()
    {
        var result = await this.RunAsync("""{"action":"list"}""");

        Assert.True(result.Success);
        Assert.Contains("No timers", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_separates_pending_from_fired_and_says_which_count_against_the_limit()
    {
        // A bare "3 timer(s)" leaves the agent unable to work out how much room it has left.
        this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "gone", description: "already");
        this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "still coming", description: "later");
        this.time.Advance(TimeSpan.FromSeconds(1));

        var result = await this.RunAsync("""{"action":"list"}""");

        Assert.True(result.Success);
        Assert.Contains("1 pending", result.Content, StringComparison.Ordinal);
        Assert.Contains("1 already fired", result.Content, StringComparison.Ordinal);
        Assert.Contains(
            $"limit of {SessionTimerService.PerConversationCap}",
            result.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_shows_a_fired_timer_as_uncancellable_and_how_long_ago_it_fired()
    {
        this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "gone");
        this.time.Advance(TimeSpan.FromSeconds(1));
        this.time.Advance(TimeSpan.FromSeconds(45));

        var result = await this.RunAsync("""{"action":"list"}""");

        Assert.Contains("ALREADY FIRED", result.Content, StringComparison.Ordinal);
        Assert.Contains("cannot be cancelled", result.Content, StringComparison.Ordinal);
        Assert.Contains("45s ago", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_shows_a_pending_timer_with_its_countdown_intent_and_label()
    {
        this.service.Schedule("conv", "ch", delaySeconds: 90, intent: "call round 2", description: "round 2");

        var result = await this.RunAsync("""{"action":"list"}""");

        Assert.Contains("fires in 90s", result.Content, StringComparison.Ordinal);
        Assert.Contains("call round 2", result.Content, StringComparison.Ordinal);
        Assert.Contains("round 2", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_is_scoped_to_the_calling_conversation()
    {
        this.service.Schedule("other", "ch", delaySeconds: 600, intent: "someone else's");

        var result = await this.RunAsync("""{"action":"list"}""");

        Assert.DoesNotContain("someone else's", result.Content, StringComparison.Ordinal);
    }

    // ── cancel ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_a_pending_timer_succeeds()
    {
        var id = this.service.Schedule("conv", "ch", delaySeconds: 600, intent: "x");

        var result = await this.RunAsync($$"""{"action":"cancel","timer_id":"{{id}}"}""");

        Assert.True(result.Success);
        Assert.Empty(this.service.List("conv"));
    }

    [Fact]
    public async Task Cancelling_a_fired_timer_fails_and_explains_that_the_intent_is_already_delivered()
    {
        var id = this.service.Schedule("conv", "ch", delaySeconds: 1, intent: "x");
        this.time.Advance(TimeSpan.FromSeconds(1));

        var result = await this.RunAsync($$"""{"action":"cancel","timer_id":"{{id}}"}""");

        // Fail, not Ok: the requested action did not happen. AgentRuntime surfaces Error to the
        // model, so the explanation has to live there rather than in Content.
        Assert.False(result.Success);
        Assert.Contains("already fired", result.Error, StringComparison.OrdinalIgnoreCase);

        // And tell it what to do about it, or it is left knowing only that something failed.
        Assert.Contains("do not act on it", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelling_an_unknown_timer_fails_and_points_at_list()
    {
        var result = await this.RunAsync("""{"action":"cancel","timer_id":"nope"}""");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("list", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_timer_belonging_to_another_conversation_cannot_be_cancelled()
    {
        var id = this.service.Schedule("other", "ch", delaySeconds: 600, intent: "someone else's");

        var result = await this.RunAsync($$"""{"action":"cancel","timer_id":"{{id}}"}""");

        Assert.False(result.Success);
        Assert.Single(this.service.List("other"));
    }

    [Fact]
    public async Task Cancelling_without_a_timer_id_is_rejected()
    {
        var result = await this.RunAsync("""{"action":"cancel"}""");

        Assert.False(result.Success);
        Assert.Contains("timer_id", result.Error, StringComparison.Ordinal);
    }

    // ── dispatch ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_action_lists_the_valid_ones()
    {
        var result = await this.RunAsync("""{"action":"explode"}""");

        Assert.False(result.Success);
        Assert.Contains("create", result.Error, StringComparison.Ordinal);
        Assert.Contains("list", result.Error, StringComparison.Ordinal);
        Assert.Contains("cancel", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_arguments_are_reported_rather_than_thrown()
    {
        var result = await this.RunAsync("{not json");

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_description_tells_the_model_that_a_fired_timer_cannot_be_cancelled()
    {
        // The model picks actions from this text. If the state machine is only discoverable by
        // failing a cancel, it will keep failing cancels.
        Assert.Contains("already fired", this.tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recently fired", this.tool.Description, StringComparison.OrdinalIgnoreCase);
    }
}
