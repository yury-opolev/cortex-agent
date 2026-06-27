using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Detects when the LLM is stuck in a "doom loop" — repeatedly making
/// identical tool calls. Inspired by OpenCode's approach: if the last N
/// consecutive tool calls all have the same name and arguments, the loop
/// is considered stuck and should be stopped.
/// </summary>
internal sealed class DoomLoopDetector
{
    /// <summary>Number of identical consecutive tool calls to trigger detection.</summary>
    private const int Threshold = 3;

    private string? lastToolName;
    private string? lastToolArguments;
    private int consecutiveCount;

    /// <summary>
    /// Checks a batch of tool calls from a single LLM response.
    /// Returns <c>true</c> if a doom loop is detected (the same tool call
    /// has been repeated <see cref="Threshold"/> or more times consecutively).
    /// </summary>
    /// <param name="toolCalls">Tool calls from the current LLM response.</param>
    public bool Check(IReadOnlyList<LlmToolCall> toolCalls)
    {
        foreach (var call in toolCalls)
        {
            if (string.Equals(call.Name, this.lastToolName, StringComparison.Ordinal)
                && string.Equals(call.Arguments, this.lastToolArguments, StringComparison.Ordinal))
            {
                this.consecutiveCount++;
            }
            else
            {
                this.lastToolName = call.Name;
                this.lastToolArguments = call.Arguments;
                this.consecutiveCount = 1;
            }

            if (this.consecutiveCount >= Threshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Resets the detector state.</summary>
    public void Reset()
    {
        this.lastToolName = null;
        this.lastToolArguments = null;
        this.consecutiveCount = 0;
    }
}
