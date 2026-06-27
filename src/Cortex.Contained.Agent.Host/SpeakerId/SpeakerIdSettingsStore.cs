namespace Cortex.Contained.Agent.Host.SpeakerId;

/// <summary>Volatile runtime store for the pushed voice-id enable flag. Default enabled
/// (so existing deployments and tests behave identically until a disable is pushed).</summary>
public sealed class SpeakerIdSettingsStore
{
    private volatile bool isVoiceIdEnabled = true;

    /// <summary>Effective voice-id enablement as last pushed from the Bridge.</summary>
    public bool IsVoiceIdEnabled => this.isVoiceIdEnabled;

    /// <summary>Apply a pushed enable flag.</summary>
    public void SetEnabled(bool enabled) => this.isVoiceIdEnabled = enabled;
}
