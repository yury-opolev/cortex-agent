namespace Cortex.Contained.Contracts.SystemPrompt;

/// <summary>
/// User-editable system-prompt configuration: the two prompt templates plus the
/// authorable prose segments. Persisted per container (per tenant) by the agent.
/// </summary>
public sealed class SystemPromptConfig
{
    /// <summary>Main-agent (and scheduled/task-run) prompt template with {{placeholders}}.</summary>
    public string MainTemplate { get; set; } = SystemPromptDefaults.MainTemplate;

    /// <summary>Subagent prompt template with {{placeholders}}.</summary>
    public string SubagentTemplate { get; set; } = SystemPromptDefaults.SubagentTemplate;

    /// <summary>Authorable voice-mode block (injected only on voice channels).</summary>
    public string VoiceMode { get; set; } = SystemPromptDefaults.VoiceMode;

    /// <summary>Authorable coding-agent relay block.</summary>
    public string CodingRelay { get; set; } = SystemPromptDefaults.CodingRelay;

    /// <summary>Authorable fixed instructions block for subagents.</summary>
    public string SubagentInstructions { get; set; } = SystemPromptDefaults.SubagentInstructions;
}
