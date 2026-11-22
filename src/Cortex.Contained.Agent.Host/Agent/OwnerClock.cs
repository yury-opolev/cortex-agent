using System.Globalization;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// The owner's wall clock: a <see cref="TimeProvider"/> plus the zone the owner lives in. The zone
/// is the one the history index already uses (<c>History:TimeZone</c>, which the host feeds to the
/// container as <c>CORTEX_TIMEZONE</c>; empty means the machine's local zone), so a "yesterday" in
/// a stamp and a "yesterday" in a recall result are the same day.
/// <para>
/// Its job is to give the model the time without disturbing the provider's prompt cache. Every
/// user-role message in a prompt is rendered with a stamp derived from that message's OWN
/// timestamp (<see cref="StampUserMessages"/>), so a message produces the same bytes on every
/// request it appears in — across tool rounds, mid-turn drains, later turns and restarts. The
/// latest user message's stamp is therefore effectively "now". The system prompt only carries the
/// static <see cref="SystemPromptNote"/>.
/// </para>
/// </summary>
public sealed class OwnerClock
{
    /// <summary>
    /// Static system-prompt explanation of the stamps. Contains no date or time, so the cached
    /// prompt prefix stays byte-identical from one request to the next.
    /// </summary>
    public const string SystemPromptNote =
        "\n\n## Message timestamps\n"
        + "The current date and time are never part of this system prompt. Each user message starts "
        + "with a bracketed line giving the time it was sent, in the owner's local time zone, for "
        + "example `[Sunday 22 November 2026, 16:45 (Europe/Paris, UTC+01:00)]`. That line is "
        + "supplied by the system, not written by the user. The line on the latest user message is "
        + "the current time; use these lines to work out \"today\", \"yesterday\" and \"how long "
        + "ago\". Timestamps shown elsewhere, such as in delivery reports, are in UTC — convert "
        + "them using the offset in these lines.";

    private readonly TimeProvider time;

    public OwnerClock(TimeProvider time, TimeZoneInfo zone, bool stampEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(zone);
        this.time = time;
        this.Zone = zone;
        this.StampEnabled = stampEnabled;
    }

    /// <summary>The owner's time zone.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>Whether user messages are stamped (and the system prompt explains it) at all.</summary>
    public bool StampEnabled { get; }

    /// <summary>The current instant, from the injected <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset UtcNow => this.time.GetUtcNow();

    /// <summary>
    /// Builds the clock from the history zone. An unknown zone falls back to UTC — an honest,
    /// labelled stamp — rather than failing startup; <c>History</c> validation reports the bad value.
    /// </summary>
    public static OwnerClock FromHistoryOptions(TimeProvider time, HistoryOptions options, bool stampEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(options);

        TimeZoneInfo zone;
        try
        {
            zone = options.ResolveTimeZone();
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
        }

        return new OwnerClock(time, zone, stampEnabled);
    }

    /// <summary>
    /// The stamp line for <paramref name="instant"/>, e.g.
    /// <c>[Sunday 22 November 2026, 16:45 (Europe/Paris, UTC+01:00)]</c>. Invariant English,
    /// minute precision; the offset is that of the instant itself, so it is right across DST changes.
    /// </summary>
    public string FormatStamp(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, this.Zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var text = local.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        var utcOffset = offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        return $"[{text} ({this.ZoneName()}, UTC{sign}{utcOffset})]";
    }

    /// <summary>Appends <see cref="SystemPromptNote"/> to a system prompt when stamping is enabled.</summary>
    public string AppendSystemPromptNote(string systemPrompt) =>
        this.StampEnabled ? systemPrompt + SystemPromptNote : systemPrompt;

    /// <summary>
    /// Replaces every user-role message of an outgoing prompt with a copy that starts with the stamp
    /// of its own <see cref="LlmMessage.Timestamp"/>. Prompt-only: the list is the caller's copy, so
    /// session history, the message store and compaction input keep the raw messages.
    /// <para>
    /// Skipped for a compaction summary (not something anyone sent) and for a message with no
    /// timestamp (<c>default</c>, e.g. history seeded without times) — a made-up time would be worse
    /// than none.
    /// </para>
    /// </summary>
    public void StampUserMessages(List<LlmMessage> prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        if (!this.StampEnabled)
        {
            return;
        }

        for (var i = 0; i < prepared.Count; i++)
        {
            var message = prepared[i];
            if (message.Role != "user"
                || message.MessageType == LlmMessageType.CompactionSummary
                || message.Timestamp == default)
            {
                continue;
            }

            prepared[i] = WithStamp(message, this.FormatStamp(message.Timestamp));
        }
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

    // A Windows host reports a Windows zone id (e.g. "W. Europe Standard Time"); the model should see IANA.
    private string ZoneName()
    {
        var id = this.Zone.Id;
        if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase) || this.Zone.HasIanaId)
        {
            return id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
    }
}
