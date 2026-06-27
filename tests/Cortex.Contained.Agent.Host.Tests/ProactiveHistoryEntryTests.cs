using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Pins how a proactive / timer message is recorded into the target channel's
/// session history. User feedback 2026-05-15: it must read as a natural phrase,
/// not be wrapped in a "[proactive message sent to {channel}]" annotation.
/// </summary>
public class ProactiveHistoryEntryTests
{
    [Fact]
    public void FormatProactiveHistoryEntry_RecordsTextVerbatim_NoBracketAnnotation()
    {
        var record = new ProactiveMessageRecord
        {
            ChannelId = "discord-voice",
            Text = "Time's up! Ready for the next set?",
        };

        var entry = AgentRuntime.FormatProactiveHistoryEntry(record);

        Assert.Equal("Time's up! Ready for the next set?", entry);
        Assert.DoesNotContain("[proactive", entry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("discord-voice", entry, StringComparison.Ordinal);
    }
}
