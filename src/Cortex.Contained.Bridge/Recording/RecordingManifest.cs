using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Recording;

/// <summary>
/// Per-session manifest written into <c>manifest.json</c>. <c>EndUtc</c> is
/// null while the session is active — the crash-recovery sweep keys off
/// that to find torn artifacts. <c>GroundTruthTurns</c> is populated
/// post-hoc by the EOU eval workflow (the harness reads it and compares to
/// the <c>commit</c> events in <c>events.jsonl</c>).
/// </summary>
public sealed record RecordingManifest
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string ChannelKey { get; init; }

    public string? ChannelDisplay { get; init; }

    public required DateTimeOffset StartUtc { get; init; }

    public DateTimeOffset? EndUtc { get; init; }

    public long DurationMs { get; init; }

    public long CapMs { get; init; }

    public bool CapReached { get; init; }

    public bool Crashed { get; init; }

    public string? StopReason { get; init; }

    public IReadOnlyList<JsonElement> GroundTruthTurns { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static RecordingManifest FromJson(string json) =>
        JsonSerializer.Deserialize<RecordingManifest>(json, JsonOpts)!;
}
