namespace Cortex.Contained.Channels.Discord;

/// <summary>Classifies speech that arrived while the agent was speaking.</summary>
internal interface IInterruptClassifier
{
    /// <param name="partialTranscript">Best-effort partial of the interrupting speech.</param>
    /// <param name="pEou">Latest turn-detector P(end-of-turn), or 0.</param>
    Task<InterruptClass> ClassifyAsync(string partialTranscript, float pEou, CancellationToken ct);
}
