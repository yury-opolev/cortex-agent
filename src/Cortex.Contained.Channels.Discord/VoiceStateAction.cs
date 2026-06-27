namespace Cortex.Contained.Channels.Discord;

/// <summary>What the voice handler should do in response to a voice-state update.</summary>
public enum VoiceStateAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>The linked user is in the target channel but the bot has no live
    /// voice connection — (re)join, start receiving audio, then drain any queued
    /// proactive messages. Also the recovery path when a stale audio-client
    /// reference was left behind by a gateway reconnect.</summary>
    JoinAndDrainProactive,

    /// <summary>The linked user (re)joined while the bot already has a live
    /// connection — just drain any queued proactive messages (e.g. an
    /// in-progress proactive "ring").</summary>
    DrainProactive,

    /// <summary>The linked user left the target channel and no other non-bot
    /// users remain — leave the voice channel.</summary>
    Leave,
}
