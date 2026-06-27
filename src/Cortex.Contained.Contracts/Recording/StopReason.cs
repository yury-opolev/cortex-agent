namespace Cortex.Contained.Contracts.Recording;

/// <summary>Why a recording session stopped. Surfaces in auto_stop events and the manifest's stopReason field.</summary>
public enum StopReason
{
    /// <summary>User invoked /voice-record stop.</summary>
    Manual,

    /// <summary>60-minute cap reached and the controller auto-stopped the session.</summary>
    Cap,

    /// <summary>IHostedService graceful shutdown finalised the session.</summary>
    Shutdown,

    /// <summary>Previous Bridge process died mid-recording; startup sweep finalised the artifacts.</summary>
    Crash,
}
