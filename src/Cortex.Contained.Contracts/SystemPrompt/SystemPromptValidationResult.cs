namespace Cortex.Contained.Contracts.SystemPrompt;

/// <summary>Result of validating a <see cref="SystemPromptConfig"/> before persisting.</summary>
public sealed class SystemPromptValidationResult
{
    /// <summary>True when there are no blocking errors.</summary>
    public bool IsValid { get; set; }

    /// <summary>Blocking problems (unknown placeholder, cap exceeded). Non-empty ⇒ not saved.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>Non-blocking advisories (missing recommended placeholder).</summary>
    public List<string> Warnings { get; set; } = [];
}
