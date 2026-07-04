using System.Collections.Frozen;

namespace Cortex.Contained.Contracts.SystemPrompt;

/// <summary>Catalog of valid placeholder names and size limits for prompt templates.</summary>
public static class SystemPromptPlaceholders
{
    /// <summary>Maximum characters for a template body.</summary>
    public const int TemplateMaxChars = 8000;

    /// <summary>Maximum characters for an authorable prose segment.</summary>
    public const int SegmentMaxChars = 4000;

    /// <summary>Placeholder names allowed in the main template.</summary>
    public static readonly FrozenSet<string> Main = FrozenSet.ToFrozenSet(
        [
            "personality", "self_notes", "skills", "channel",
            "voice_mode", "active_tasks", "active_plans", "coding_relay",
        ], StringComparer.Ordinal);

    /// <summary>Placeholder names allowed in the subagent template.</summary>
    public static readonly FrozenSet<string> Subagent = FrozenSet.ToFrozenSet(
        [
            "personality", "skill", "instructions", "skills",
            "bootstrap_context", "recalled_memories",
        ], StringComparer.Ordinal);

    /// <summary>Main placeholders whose absence is worth warning about.</summary>
    public static readonly string[] MainRecommended =
        ["personality", "self_notes", "skills", "coding_relay"];

    /// <summary>Subagent placeholders whose absence is worth warning about.</summary>
    public static readonly string[] SubagentRecommended =
        ["instructions", "skills"];
}
