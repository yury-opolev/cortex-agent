namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Data for voice pipeline state transitions.
/// Used by the desktop overlay and diagnostics to react to state changes.
/// </summary>
/// <param name="PreviousState">The state the voice pipeline was in before the transition.</param>
/// <param name="NewState">The state the voice pipeline transitioned to.</param>
public record VoiceStateChange(VoiceState PreviousState, VoiceState NewState);
