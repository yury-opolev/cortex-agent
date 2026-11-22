using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Recall;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Contained.Integration.Tests;

/// <summary>
/// Host-startup composition guard: boots the real Agent Host DI graph (Program.cs) and resolves the erasure
/// steps, the purge/recall services and the hub, so a construction-time cycle or a missing registration
/// fails here instead of hanging or crashing the host at startup.
/// </summary>
public sealed class AgentHostCompositionTests : IClassFixture<AgentHostFactory>
{
    private readonly AgentHostFactory factory;

    public AgentHostCompositionTests(AgentHostFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void ErasureSteps_ResolveIncludingDependentMemories()
    {
        var steps = this.factory.Services.GetServices<IErasureStep>().Select(s => s.Name).ToList();

        Assert.Contains(ErasureSteps.DependentMemories, steps);
        Assert.IsType<DependentMemoryErasureStep>(
            this.factory.Services.GetServices<IErasureStep>().Single(s => s.Name == ErasureSteps.DependentMemories));
        Assert.NotNull(this.factory.Services.GetRequiredService<HistoryPurgeService>());
    }

    [Fact]
    public void RecallServices_Resolve()
    {
        var recall = this.factory.Services.GetRequiredService<RecallControlService>();

        Assert.NotNull(this.factory.Services.GetRequiredService<QuarantineReviewService>());
        Assert.NotNull(recall.GetSettings());
    }

    [Fact]
    public async Task AgentHub_ResolvesWithTheRecallService()
    {
        using var scope = this.factory.Services.CreateScope();

        var hub = ActivatorUtilities.CreateInstance<AgentHub>(scope.ServiceProvider);

        Assert.NotNull(await hub.GetRecallSettings());
    }
}
