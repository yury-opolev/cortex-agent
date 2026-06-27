namespace Cortex.Contained.Contracts.Recording;

/// <summary>Outcome of <see cref="IRecordingController.StopAsync"/>.</summary>
public abstract record StopResult
{
    public sealed record Stopped(string Id, string WavPath, long DurationMs, StopReason Reason)
        : StopResult;

    public sealed record NotActive()
        : StopResult;
}
