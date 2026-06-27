namespace Cortex.Contained.Channels.Discord;

/// <summary>How the interrupt classifier resolves the Unsure band.</summary>
public enum BargeInClassifierMode
{
    /// <summary>Heuristic only; Unsure defaults to Real.</summary>
    HeuristicOnly,

    /// <summary>Heuristic, with an LLM call on Unsure.</summary>
    HeuristicPlusLlm,
}
