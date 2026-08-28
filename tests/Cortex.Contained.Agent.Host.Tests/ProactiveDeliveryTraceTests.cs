using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Pins the delivery trace written into a channel's session history when a scheduled task or a
/// fired timer sends it a message.
/// <para>
/// Telemetry 2026-08-28 (/app/state/messages.db): the delivered text used to be appended as a bare
/// assistant message, and the sending run happens in a session the target conversation cannot see.
/// With no tool call behind it the model concluded it had invented the message and retracted true
/// ones to the user — "those sleep numbers and the image reference were not real; I made no sleep
/// or health tool call" (2026-08-28 06:00:04), three such apologies inside five minutes. It had
/// already diagnosed itself on 2026-08-22: "the morning task runs in its own session, so its
/// message lands here with no tool trace I can see — and I read that absence as proof of
/// invention."
/// </para>
/// </summary>
public class ProactiveDeliveryTraceTests
{
    private static readonly ProactiveMessageRecord Sleep = new()
    {
        ChannelId = "discord-dm",
        Text = "Last night's sleep: 6h 25m total.",
    };

    private static IReadOnlyList<LlmMessage> Group(string? trigger = "scheduled task ca7ed0cd (\"morning sleep check\")")
        => AgentRuntime.BuildProactiveDeliveryGroup(
            Sleep, trigger, new DateTimeOffset(2026, 8, 28, 5, 30, 49, TimeSpan.Zero));

    [Fact]
    public void The_delivered_text_has_a_tool_call_behind_it()
    {
        var group = Group();

        var call = Assert.Single(group, m => m.ToolCalls is { Count: > 0 });
        var toolCall = Assert.Single(call.ToolCalls!);
        Assert.Equal("send_message", toolCall.Name);
        Assert.Contains("discord-dm", toolCall.Arguments, StringComparison.Ordinal);
        Assert.Contains("6h 25m", toolCall.Arguments, StringComparison.Ordinal);

        // The result must be matched to the call, or the provider clients strip the pair as an
        // orphan and the trace silently disappears — the exact failure it exists to prevent.
        var result = Assert.Single(group, m => m.Role == "tool");
        Assert.Equal(toolCall.Id, result.ToolCallId);
    }

    [Fact]
    public void The_receipt_says_the_message_is_real_and_already_received()
    {
        var receipt = Assert.Single(Group(), m => m.Role == "tool").Content;

        Assert.Contains("Delivered to discord-dm", receipt, StringComparison.Ordinal);
        Assert.Contains("already received", receipt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not retract", receipt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_trigger_is_named_so_the_missing_lead_up_is_explained()
    {
        var notice = Assert.Single(Group(), m => m.Role == "user").Content;

        Assert.Contains("ca7ed0cd", notice, StringComparison.Ordinal);
        Assert.Contains("its own session", notice, StringComparison.OrdinalIgnoreCase);

        // It occupies the user slot without the user having said anything, so it has to say so.
        Assert.Contains("the user did not say this", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_trigger_still_produces_a_well_formed_notice()
    {
        // Null trigger = a plain cross-channel send from a normal turn: still invisible here, but
        // it did not "fire" and did not run in its own session.
        var notice = Assert.Single(Group(trigger: null), m => m.Role == "user").Content;

        Assert.False(string.IsNullOrWhiteSpace(notice));
        Assert.Contains("another conversation", notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fired", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_the_delivered_text_is_visible_to_the_user()
    {
        var group = Group();

        var visible = group.Where(m => !m.IsInternal).ToList();
        var only = Assert.Single(visible);
        Assert.Equal("assistant", only.Role);
        Assert.Equal(Sleep.Text, only.Content);
        Assert.Equal(LlmMessageType.Proactive, only.MessageType);

        // The 2026-05-15 rule still holds: the explanation lives in the trace, never in the text.
        Assert.DoesNotContain("[", only.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("discord-dm", only.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_group_is_a_well_formed_turn_wherever_it_lands()
    {
        // The target conversation almost always ends on an assistant message, and OpenAI/Copilot
        // reject two assistant messages in a row — so the group must not open on one.
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
        // The glue is what taught the model to announce a timer the instant it set one: it merged
        // the fired cue into the turn that started the rest, leaving one message reading
        // "…ninety seconds resting. / Rest is up." for the model to copy in a single turn
        // (2026-08-24: "I jumped the gun a moment ago", "I called it early again").
        var history = new ConversationHistory();
        history.Add(new LlmMessage { Role = "user", Content = "set one done" });
        history.Add(new LlmMessage { Role = "assistant", Content = "Logged. Ninety seconds resting." });

        history.AddGroup(AgentRuntime.BuildProactiveDeliveryGroup(
            new ProactiveMessageRecord { ChannelId = "discord-voice", Text = "Rest is up. Seated row." },
            "timer 7c4008bc, set 90s earlier",
            DateTimeOffset.UtcNow));

        var messages = history.Snapshot();
        Assert.Equal("Logged. Ninety seconds resting.", messages[1].Content);
        Assert.DoesNotContain(messages, m => m.Content is { } c
            && c.Contains("Ninety seconds resting.", StringComparison.Ordinal)
            && c.Contains("Rest is up.", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_delivery_gets_its_own_tool_call_id()
    {
        var first = Group().Single(m => m.ToolCalls is { Count: > 0 }).ToolCalls![0].Id;
        var second = Group().Single(m => m.ToolCalls is { Count: > 0 }).ToolCalls![0].Id;

        // Duplicate ids inside one conversation are rejected by Anthropic.
        Assert.NotEqual(first, second);
    }
}
