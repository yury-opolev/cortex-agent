using System.Text.Json.Serialization;

namespace Cortex.Contained.ScenarioEvals.Model;

/// <summary>
/// Top-level scenario definition deserialized from a JSON file.
/// Each scenario represents a synthetic persona interacting with the agent across multiple phases.
/// </summary>
public sealed class ScenarioDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("persona")]
    public required PersonaDefinition Persona { get; init; }

    /// <summary>
    /// Known facts about the persona, grouped by category (e.g., "career", "hobbies").
    /// Used for scoring recall and memory extraction accuracy.
    /// </summary>
    [JsonPropertyName("facts")]
    public required Dictionary<string, string[]> Facts { get; init; }

    [JsonPropertyName("phases")]
    public required PhaseDefinition[] Phases { get; init; }
}

public sealed class PersonaDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("background")]
    public required string Background { get; init; }

    [JsonPropertyName("personality")]
    public required string Personality { get; init; }
}

/// <summary>
/// A phase simulates a distinct interaction period (e.g., "Day 1", "Day 2").
/// Lifecycle events between phases simulate time passing and system maintenance.
/// </summary>
public sealed class PhaseDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("segments")]
    public required SegmentDefinition[] Segments { get; init; }

    /// <summary>
    /// Lifecycle events to execute after all segments complete.
    /// Valid values: "compact", "compact-memories", "reset-session".
    /// </summary>
    [JsonPropertyName("after")]
    public string[] After { get; init; } = [];
}

/// <summary>
/// A segment within a phase — a conversation topic, task request, or pause.
/// </summary>
public sealed class SegmentDefinition
{
    /// <summary>
    /// Segment type: "conversation" (default), "task", "schedule", "pause".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "conversation";

    [JsonPropertyName("topic")]
    public required string Topic { get; init; }

    [JsonPropertyName("exchanges")]
    public int Exchanges { get; init; } = 3;

    /// <summary>
    /// Optional hints the actor should weave into conversation (e.g., "mention you use VS Code").
    /// </summary>
    [JsonPropertyName("hints")]
    public string[] Hints { get; init; } = [];

    [JsonPropertyName("scoring")]
    public ScoringCriteria? Scoring { get; init; }
}

/// <summary>
/// Defines what to score for a segment or phase.
/// All fields are optional — only specified dimensions are evaluated.
/// </summary>
public sealed class ScoringCriteria
{
    /// <summary>
    /// Facts the agent should recall in its responses (exact or close match).
    /// </summary>
    [JsonPropertyName("recall_facts")]
    public string[] RecallFacts { get; init; } = [];

    /// <summary>
    /// Softer facts — nice to recall but not required for passing.
    /// </summary>
    [JsonPropertyName("soft_facts")]
    public string[] SoftFacts { get; init; } = [];

    [JsonPropertyName("no_hallucination")]
    public bool NoHallucination { get; init; }

    [JsonPropertyName("naturalness")]
    public bool Naturalness { get; init; }

    [JsonPropertyName("task_completed")]
    public bool TaskCompleted { get; init; }

    /// <summary>
    /// Strings the agent response should contain (for task verification).
    /// </summary>
    [JsonPropertyName("response_contains")]
    public string[] ResponseContains { get; init; } = [];

    /// <summary>
    /// Facts that should be present in the agent's memory store.
    /// </summary>
    [JsonPropertyName("memory_facts")]
    public string[] MemoryFacts { get; init; } = [];

    /// <summary>
    /// Human-readable label for this scoring checkpoint.
    /// </summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}
