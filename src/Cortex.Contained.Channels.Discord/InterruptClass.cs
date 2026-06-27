namespace Cortex.Contained.Channels.Discord;

/// <summary>Verdict for speech that arrived during agent playback.</summary>
public enum InterruptClass
{
    /// <summary>No classification applicable.</summary>
    None,

    /// <summary>Genuine take-the-floor intent.</summary>
    Real,

    /// <summary>Acknowledgement / "mhm" / laughter — not taking the floor.</summary>
    Backchannel,

    /// <summary>Heuristic inconclusive; escalate to the LLM tier.</summary>
    Unsure,
}
