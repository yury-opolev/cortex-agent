namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Tracks which Discord text channel issued a <c>/voice-id enroll</c> command for a
/// given tenant and posts state-appropriate progress messages as the enrollment
/// wizard advances.
/// </summary>
public interface IEnrollmentProgressNotifier
{
    /// <summary>Remember which Discord text channel issued the enroll command for this tenant.</summary>
    void TrackInteractionChannel(string tenantId, ulong textChannelId);

    /// <summary>Stop tracking. Called on terminal states (Enrolled, Unknown, Declined).</summary>
    void Untrack(string tenantId);

    /// <summary>Post a state-appropriate progress message to the tracked channel.</summary>
    ValueTask ReportAsync(string tenantId, string stateName, int captured, int required);
}
