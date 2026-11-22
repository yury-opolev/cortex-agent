using System.Globalization;
using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// The owner's wall clock: a <see cref="TimeProvider"/> plus the zone the owner lives in. The zone
/// is the one the history index already uses (<c>History:TimeZone</c>, which the host feeds to the
/// container as <c>CORTEX_TIMEZONE</c>; empty means the machine's local zone), so a "yesterday" in
/// a stamp and a "yesterday" in a recall result are the same day.
/// <para>
/// Its job is to put the current time in front of the model without disturbing the provider's
/// prompt cache: the system prompt only carries <see cref="SystemPromptNote"/> (identical on every
/// request), while the changing <see cref="FormatStamp()"/> line is attached by
/// <see cref="TurnTimeStamper"/> to the final user message of a single request.
/// </para>
/// </summary>
public sealed class OwnerClock
{
    /// <summary>
    /// Static system-prompt explanation of the stamp. Contains no date or time, so the cached
    /// prompt prefix stays byte-identical from one request to the next.
    /// </summary>
    public const string SystemPromptNote =
        "\n\n## Current time\n"
        + "The current date and time are never part of this system prompt. The latest message of "
        + "each turn — what the user just said, or the trigger of a scheduled or automated run — "
        + "starts with a bracketed line such as `[Current time: ...]`. That line is supplied by the "
        + "system, not written by the user, and is authoritative for what \"now\", \"today\", "
        + "\"yesterday\" and \"how long ago\" mean. Earlier messages do not repeat it; any "
        + "timestamps shown inside them (some, such as delivery reports, are in UTC) say when "
        + "those messages happened, so compare them with the latest stamp using its UTC offset.";

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

    /// <summary>Whether the current-time stamp (and its system-prompt note) is added at all.</summary>
    public bool StampEnabled { get; }

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

    /// <summary>The stamp line for the current instant.</summary>
    public string FormatStamp() => this.FormatStamp(this.time.GetUtcNow());

    /// <summary>
    /// The stamp line for <paramref name="instant"/>, e.g.
    /// <c>[Current time: Sunday 22 November 2026, 16:45 (Europe/Paris, UTC+01:00)]</c>. Invariant
    /// English, minute precision.
    /// </summary>
    public string FormatStamp(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, this.Zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var text = local.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        var utcOffset = offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        return $"[Current time: {text} ({this.ZoneName()}, UTC{sign}{utcOffset})]";
    }

    /// <summary>Appends <see cref="SystemPromptNote"/> to a system prompt when stamping is enabled.</summary>
    public string AppendSystemPromptNote(string systemPrompt) =>
        this.StampEnabled ? systemPrompt + SystemPromptNote : systemPrompt;

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
