using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Pins what is written into a channel's session history when a scheduled task, a fired timer or
/// another conversation delivers it a message.
/// <para>
/// Telemetry 2026-08-28 (/app/state/messages.db): the delivered text used to be appended as a bare
/// assistant message, and the sending run happens in a session the target conversation cannot see.
/// With nothing behind it the model concluded it had invented the message and retracted true ones
/// ("those sleep numbers and the image reference were not real; I made no sleep or health tool
/// call", 2026-08-28 06:00:04). The first fix reconstructed a <c>send_message</c> call in the target
/// conversation — a forged trace. Increment 2 replaces it with a system-recorded delivery report
/// rendered from the event log, with evidence IDs that resolve through <c>event_get</c>.
/// </para>
/// </summary>
public class ProactiveDeliveryTraceTests
{
    private static readonly ProactiveMessageRecord Sleep = new()
    {
        ChannelId = "discord-dm",
        Text = "Last night's sleep: 6h 25m total.",
        OperationId = "op_1",
        DeliveredAt = new DateTimeOffset(2026, 8, 28, 5, 30, 49, TimeSpan.Zero),
    };

    private static DeliveryReport Report(string state = "acknowledged") => new()
    {
        ChannelId = "discord-dm",
        AttemptedAt = new DateTimeOffset(2026, 8, 28, 5, 30, 49, TimeSpan.Zero),
        State = state,
        ProducerKind = AuthorKinds.ScheduledTask,
        ProducerId = "ca7ed0cd",
        ProducerLabel = "scheduled task ca7ed0cd (\"morning sleep check\")",
        RunId = "run_1",
        OperationId = "op_1",
        DeliveryEventId = "ev_delivery",
        Evidence = [new DeliveryEvidence("ev_sleep", "mcp__fitbit__sleep", true, null)],
    };

    private static IReadOnlyList<LlmMessage> Group(DeliveryReport? report = null) =>
        AgentRuntime.BuildProactiveDeliveryGroup(Sleep, report ?? Report());

    [Fact]
    public void The_group_contains_no_reconstructed_tool_call()
    {
        var group = Group();

        Assert.DoesNotContain(group, m => m.ToolCalls is { Count: > 0 });
        Assert.DoesNotContain(group, m => m.Role == "tool");
    }

    [Fact]
    public void The_report_is_labelled_as_system_recorded_and_names_producer_run_and_evidence()
    {
        var report = Assert.Single(Group(), m => m.Role == "user").Content!;

        Assert.StartsWith(DeliveryReportRenderer.Label, report, StringComparison.Ordinal);
        Assert.Contains("the user did not write this", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ca7ed0cd", report, StringComparison.Ordinal);
        Assert.Contains("run_1", report, StringComparison.Ordinal);
        Assert.Contains("ev_sleep", report, StringComparison.Ordinal);
        Assert.Contains("do not retract", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unacknowledged_send_is_not_presented_as_certainly_delivered()
    {
        var report = Assert.Single(Group(Report("unknown")), m => m.Role == "user").Content!;

        Assert.Contains("send state: unknown", report, StringComparison.Ordinal);
        Assert.Contains("may not have reached", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_delivered_text_is_visible_to_the_user()
    {
        var visible = Group().Where(m => !m.IsInternal).ToList();

        var only = Assert.Single(visible);
        Assert.Equal("assistant", only.Role);
        Assert.Equal(Sleep.Text, only.Content);
        Assert.Equal(LlmMessageType.Proactive, only.MessageType);

        // The 2026-05-15 rule still holds: the explanation lives in the report, never in the text.
        Assert.DoesNotContain("[", only.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_group_is_a_well_formed_turn_wherever_it_lands()
    {
        var history = new ConversationHistory();
        history.Add(new LlmMessage { Role = "user", Content = "morning" });
        history.Add(new LlmMessage { Role = "assistant", Content = "Logged your breakfast." });

        history.AddGroup(Group());

        var messages = history.Snapshot();
        for (var i = 1; i < messages.Count; i++)
        {
            Assert.False(
                messages[i].Role == "assistant" && messages[i - 1].Role == "assistant",
                $"consecutive assistant messages at {i - 1}/{i}");
        }
    }

    [Fact]
    public void The_delivered_text_is_never_glued_onto_the_previous_reply()
    {
        var history = new ConversationHistory();
        history.Add(new LlmMessage { Role = "user", Content = "set one done" });
        history.Add(new LlmMessage { Role = "assistant", Content = "Logged. Ninety seconds resting." });

        history.AddGroup(AgentRuntime.BuildProactiveDeliveryGroup(
            new ProactiveMessageRecord { ChannelId = "discord-voice", Text = "Rest is up. Seated row." },
            Report()));

        var messages = history.Snapshot();
        Assert.Equal("Logged. Ninety seconds resting.", messages[1].Content);
        Assert.DoesNotContain(messages, m => m.Content is { } c
            && c.Contains("Ninety seconds resting.", StringComparison.Ordinal)
            && c.Contains("Rest is up.", StringComparison.Ordinal));
    }
}
