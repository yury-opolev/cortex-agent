using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Attaches the owner's current-time stamp to the final user message of a prompt, for one turn (or
/// one subagent run). The stamp lives only in the request being built: <see cref="Apply"/> swaps a
/// copy into the prepared list, so session history, the message store and the extraction buffer
/// never see it, and the next request renders that same message without it — which is what keeps
/// the provider's cached prompt prefix stable.
/// <para>
/// The stamp is pinned to the message it was first applied to. Tool rounds re-build the prompt
/// many times; re-reading the clock each round would rewrite the stamped message on every minute
/// boundary and invalidate the cache for everything after it mid-turn.
/// </para>
/// </summary>
internal sealed class TurnTimeStamper
{
    private readonly OwnerClock? clock;
    private LlmMessage? pinnedMessage;
    private string? pinnedStamp;

    public TurnTimeStamper(OwnerClock? clock) => this.clock = clock;

    /// <summary>Stamps the last user-role message in <paramref name="prepared"/>, in place in the list.</summary>
    public void Apply(List<LlmMessage> prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        if (this.clock is not { StampEnabled: true })
        {
            return;
        }

        var index = prepared.FindLastIndex(m => m.Role == "user");
        if (index < 0)
        {
            return;
        }

        var message = prepared[index];
        if (this.pinnedStamp is null || !message.Equals(this.pinnedMessage))
        {
            this.pinnedMessage = message;
            this.pinnedStamp = this.clock.FormatStamp();
        }

        prepared[index] = WithStamp(message, this.pinnedStamp);
    }

    private static LlmMessage WithStamp(LlmMessage message, string stamp)
    {
        if (message.ContentBlocks is { Count: > 0 } blocks)
        {
            return message with { ContentBlocks = [LlmContentBlock.TextBlock(stamp), .. blocks] };
        }

        return message with
        {
            Content = string.IsNullOrEmpty(message.Content) ? stamp : stamp + "\n" + message.Content,
        };
    }
}
