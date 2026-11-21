namespace Cortex.Contained.Agent.Host.Events;

/// <summary>
/// The run the current async flow belongs to. Set once where a run starts (the runtime's message
/// processing, a subagent execution); read by the writers that record events, so no call
/// signature between them has to carry it.
/// </summary>
public static class RunScope
{
    private static readonly AsyncLocal<RunProvenance?> current = new();

    /// <summary>The current run, or null outside any run.</summary>
    public static RunProvenance? Current => current.Value;

    /// <summary>Makes <paramref name="run"/> current until the returned scope is disposed.</summary>
    public static IDisposable Begin(RunProvenance run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var previous = current.Value;
        current.Value = run;
        return new Restore(previous);
    }

    private sealed class Restore(RunProvenance? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}
