using System.Text.Json.Serialization;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.ScenarioEvals.HistoryGate;

/// <summary>
/// One behaviour-gate scenario (§14.3): a synthetic history fixture, ≥ 10 paraphrased user turns,
/// the outcome to measure and a rubric for the judge. Fixtures use neutral placeholders only.
/// </summary>
public sealed class GateScenario
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// What a trial measures: <c>retracted</c>, <c>acknowledgedError</c>, <c>citedSource</c>,
    /// <c>saidNever</c>, <c>statedCoverageLimit</c>, <c>recallCalled</c>, <c>presentedLatest</c>,
    /// <c>quotedDeleted</c>, <c>followedInjection</c>, <c>answeredCorrectly</c>.
    /// </summary>
    [JsonPropertyName("measure")]
    public required string Measure { get; init; }

    /// <summary>True when the measured flag being true is the desired outcome.</summary>
    [JsonPropertyName("desired")]
    public bool Desired { get; init; } = true;

    /// <summary>Synthetic history imported before every trial.</summary>
    [JsonPropertyName("fixture")]
    public required GateFixtureMessage[] Fixture { get; init; }

    /// <summary>Optional deletion run after the import (scenario 9).</summary>
    [JsonPropertyName("purge")]
    public GatePurge? Purge { get; init; }

    /// <summary>Paraphrased user turns (≥ 10).</summary>
    [JsonPropertyName("prompts")]
    public required string[] Prompts { get; init; }

    /// <summary>Ground truth and grading rule for the judge.</summary>
    [JsonPropertyName("rubric")]
    public required string Rubric { get; init; }

    /// <summary>Marker whose presence in self-notes or the reply means an injection was followed.</summary>
    [JsonPropertyName("injectionMarker")]
    public string? InjectionMarker { get; init; }

    /// <summary>Wait for a later assistant message (subagent completion) before judging.</summary>
    [JsonPropertyName("awaitFollowUpSeconds")]
    public int AwaitFollowUpSeconds { get; init; }
}

/// <summary>A fixture message; time is relative to the trial start.</summary>
public sealed class GateFixtureMessage
{
    [JsonPropertyName("daysAgo")]
    public double DaysAgo { get; init; }

    [JsonPropertyName("channel")]
    public string Channel { get; init; } = "discord-dm";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter<MessageCategory>))]
    public MessageCategory Category { get; init; } = MessageCategory.Normal;

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>Tool names used by this assistant turn (e.g. <c>history_search</c> for a recall echo).</summary>
    [JsonPropertyName("tools")]
    public string[] Tools { get; init; } = [];

    /// <summary>
    /// Converts to an import entry. The token <c>{api}</c> in <see cref="Channel"/> becomes the
    /// tenant's API channel so the fixture can seed the conversation under test.
    /// </summary>
    public ExportMessageEntry ToEntry(DateTimeOffset now, string apiChannel) => new()
    {
        UserId = this.Role == "user" ? "gate-user" : "assistant",
        ChannelId = this.Channel.Replace("{api}", apiChannel, StringComparison.Ordinal),
        Role = this.Role,
        Content = this.Text,
        Timestamp = now.AddDays(-this.DaysAgo),
        Category = this.Category,
        ToolCalls = this.Tools.Length == 0
            ? null
            : "[" + string.Join(",", this.Tools.Select(t => $"{{\"name\":\"{t}\",\"args\":\"\",\"ok\":true,\"pos\":\"after\"}}")) + "]",
    };
}

/// <summary>Deletion applied after import.</summary>
public sealed class GatePurge
{
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }
}
