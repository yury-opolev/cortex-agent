namespace Cortex.Contained.Channels.Discord;

/// <summary>Where a committed utterance should be routed.</summary>
public enum UtteranceRoute
{
    /// <summary>Route the utterance through the normal verify/STT/dispatch pipeline.</summary>
    NormalDispatch,

    /// <summary>Route the utterance to enrollment capture only (the agent is bypassed).</summary>
    EnrollmentCapture,
}
