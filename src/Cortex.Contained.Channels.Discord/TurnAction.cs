namespace Cortex.Contained.Channels.Discord;

/// <summary>What the arbiter should do in response to a turn event.</summary>
public enum TurnAction
{
    /// <summary>Do nothing.</summary>
    Ignore,

    /// <summary>Keep accumulating the user's audio (normal listening).</summary>
    Accumulate,

    /// <summary>Cancel the just-made commit; merge new audio into the same user turn.</summary>
    CancelCommitAndReabsorb,

    /// <summary>Cancel the in-flight LLM generation; merge new audio into the same user turn.</summary>
    CancelGenAndReabsorb,

    /// <summary>Stop TTS immediately; await the classifier verdict.</summary>
    StopPlaybackPendingClassify,

    /// <summary>Backchannel — replay the interrupted sentence from its start.</summary>
    ResumeFromSentenceStart,

    /// <summary>Real interrupt — truncate history and take the user's turn.</summary>
    CommitInterrupt,
}
