namespace Cortex.Contained.Channels.Discord;

/// <summary>Where the agent believes the turn is, per voice session.</summary>
public enum ConversationPhase
{
    /// <summary>User may be speaking; agent silent. Normal accumulation.</summary>
    Listening,

    /// <summary>End-of-turn detector committed; brief hold before/at LLM dispatch.</summary>
    Committed,

    /// <summary>LLM generating; no audio emitted to the user yet.</summary>
    Thinking,

    /// <summary>TTS audio is playing to the user.</summary>
    Speaking,
}
