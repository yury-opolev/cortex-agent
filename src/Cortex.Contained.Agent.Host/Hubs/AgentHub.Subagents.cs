namespace Cortex.Contained.Agent.Host.Hubs;

using Cortex.Contained.Contracts.Hub;

public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task<SubagentObservabilitySnapshot> GetSubagentSnapshots(SubagentSnapshotQuery query)
    {
        var snapshot = this.subagentObservability.GetSnapshot(query ?? new SubagentSnapshotQuery());
        return Task.FromResult(snapshot);
    }
}
