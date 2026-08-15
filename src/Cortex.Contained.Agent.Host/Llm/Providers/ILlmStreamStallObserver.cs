namespace Cortex.Contained.Agent.Host.Llm.Providers;

/// <summary>
/// Sink for inactivity-watchdog breaches. Keeps <see cref="LlmStreamIdleGuard"/> — a pure,
/// static stream decorator — free of a logger and a metrics singleton, and lets the guard be
/// unit-tested by observing reports rather than scraping log output.
/// <para>
/// Implementations must not throw and must not block: the guard is mid-breach and still has a
/// provider read to unwind.
/// </para>
/// </summary>
internal interface ILlmStreamStallObserver
{
    /// <summary>Called once per breach, immediately before the guard throws.</summary>
    void OnStall(LlmStreamStallReport report);
}
