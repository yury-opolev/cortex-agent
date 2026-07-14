using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Contracts.Tests.Observability;

public class SubagentHubInterfaceTests
{
    [Fact]
    public void AgentHub_Composes_SubagentHub()
    {
        Assert.Contains(typeof(ISubagentHub), typeof(IAgentHub).GetInterfaces());
    }

    [Fact]
    public void SubagentHub_Exposes_GetSubagentSnapshots()
    {
        var method = typeof(ISubagentHub).GetMethod(nameof(ISubagentHub.GetSubagentSnapshots));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<SubagentObservabilitySnapshot>), method!.ReturnType);
        Assert.Equal(
            [typeof(SubagentSnapshotQuery)],
            method.GetParameters().Select(p => p.ParameterType).ToArray());
    }
}
