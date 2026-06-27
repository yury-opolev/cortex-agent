namespace Cortex.Contained.Channels.Discord;

/// <summary>Inputs to the turn-arbitration state machine.</summary>
public enum TurnEvent
{
    /// <summary>The linked user started speaking.</summary>
    UserSpeechOnset,

    /// <summary>End-of-turn detector decided the user finished.</summary>
    UserCommit,

    /// <summary>The user turn was dispatched to the LLM.</summary>
    AgentSentToLlm,

    /// <summary>The first TTS audio frame for this answer was emitted.</summary>
    AgentFirstAudio,

    /// <summary>The agent answer finished playing normally.</summary>
    AgentFinished,

    /// <summary>The interrupt classifier produced a verdict.</summary>
    ClassifierResult,
}
