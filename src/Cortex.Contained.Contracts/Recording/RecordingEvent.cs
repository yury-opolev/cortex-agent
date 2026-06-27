using System.Text.Json.Nodes;

namespace Cortex.Contained.Contracts.Recording;

/// <summary>
/// A single line in the per-session <c>events.jsonl</c>. Built via the static
/// factories below — fields per <c>type</c> are documented in the spec §5.
/// Forward-compatible: readers ignore unknown fields, so additive new event
/// kinds or new fields on existing kinds don't break older fixtures.
/// </summary>
public sealed record RecordingEvent(long T, string WallUtc, string Type, JsonObject Payload)
{
    /// <summary>Render as one JSONL line (no trailing newline — the writer adds it).</summary>
    public string ToJsonLine()
    {
        var o = new JsonObject();
        o["t"] = this.T;
        o["wallUtc"] = this.WallUtc;
        o["type"] = this.Type;
        foreach (var kv in this.Payload)
        {
            o[kv.Key] = kv.Value?.DeepClone();
        }

        return o.ToJsonString();
    }

    public static RecordingEvent SessionStart(long t, string wallUtc,
        string channelKey, string label, long capMs)
        => new(t, wallUtc, "session_start", new JsonObject
        {
            ["channelKey"] = channelKey,
            ["label"] = label,
            ["capMs"] = capMs,
        });

    public static RecordingEvent AudioStart(long t, string wallUtc)
        => new(t, wallUtc, "audio_start", new JsonObject());

    public static RecordingEvent Commit(long t, string wallUtc,
        double? silenceMs, double? pEou, string reason, string utteranceId,
        string text, long audioStartMs, long audioEndMs)
    {
        var payload = new JsonObject
        {
            ["reason"] = reason,
            ["utteranceId"] = utteranceId,
            ["text"] = text,
            ["audioOffsetMs"] = new JsonObject
            {
                ["start"] = audioStartMs,
                ["end"] = audioEndMs,
            },
        };

        // Omit when not available — readers should treat absence as "no data".
        // V1 taps don't have these from the existing pipeline; future events
        // emitted closer to the EOU detector can populate them.
        if (silenceMs is { } sm)
        {
            payload["silenceMs"] = sm;
        }

        if (pEou is { } pe)
        {
            payload["pEou"] = pe;
        }

        return new RecordingEvent(t, wallUtc, "commit", payload);
    }

    public static RecordingEvent CapWarning(long t, string wallUtc, long elapsedMs, long capMs)
        => new(t, wallUtc, "cap_warning", new JsonObject
        {
            ["elapsedMs"] = elapsedMs,
            ["capMs"] = capMs,
        });

    public static RecordingEvent AutoStop(long t, string wallUtc, StopReason reason)
        => new(t, wallUtc, "auto_stop", new JsonObject
        {
            ["reason"] = reason.ToString().ToLowerInvariant(),
        });
}
