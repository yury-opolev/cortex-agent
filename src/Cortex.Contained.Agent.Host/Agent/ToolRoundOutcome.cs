namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Outcome of executing one tool round, telling the tool loop how to proceed.
/// </summary>
internal enum ToolRoundOutcome
{
    /// <summary>The round completed; the caller should loop for the next LLM call (<c>continue</c>).</summary>
    Continue,

    /// <summary>A doom loop was detected and the halt message was already delivered; the caller should stop the turn (<c>return</c>).</summary>
    DoomHalted,
}
