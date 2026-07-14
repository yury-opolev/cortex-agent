namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// SignalR hub methods exposed by the agent (inside Docker container) for generic,
/// content-free subagent worker-pool observability. Bridge → Agent direction: the Bridge
/// calls this to power the live <c>/api/tenants/{tenantId}/operations/subagents</c> endpoint.
/// Part of the composed <see cref="IAgentHub"/> surface — this method shares the single
/// SignalR hub connection and routes by method name. NEVER returns prompt, message history,
/// result, or eval text — see <see cref="SubagentWorkerSnapshot"/>.
/// </summary>
public interface ISubagentHub
{
    /// <summary>
    /// Bridge → Agent. Returns the current subagent worker snapshot (page + pool-wide
    /// aggregate) for <paramref name="query"/>.
    /// </summary>
    Task<SubagentObservabilitySnapshot> GetSubagentSnapshots(SubagentSnapshotQuery query);
}
