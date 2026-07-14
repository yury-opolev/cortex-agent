using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Regression guard for the 0.2.315 production-down incident: the agent host hung at startup because
/// <see cref="SubagentExecutionCoordinator"/>'s registration eagerly resolved <see cref="ToolRegistry"/>
/// during construction. <see cref="ToolRegistry"/> aggregates every <see cref="IAgentTool"/> — including
/// the SubAgent orchestration tools, which each depend on the coordinator — so eager resolution formed a
/// construction-time cycle (coordinator -> ToolRegistry -> subagent tool -> coordinator). That cycle
/// deadlocked host startup before Kestrel bound, and no unit test caught it because none composed the
/// full DI graph. The fix resolves ToolRegistry lazily (at first subagent dispatch), which breaks the
/// cycle. This test reproduces the exact triangle in a real container and asserts it composes.
/// </summary>
public sealed class SubagentHostCompositionTests
{
    // A stand-in for the SubAgent*Tool family: an IAgentTool that depends on the coordinator, which is
    // what closes the cycle through ToolRegistry's IEnumerable<IAgentTool> aggregation.
    private sealed class CoordinatorDependentProbeTool : IAgentTool
    {
        public CoordinatorDependentProbeTool(SubagentExecutionCoordinator coordinator)
        {
            ArgumentNullException.ThrowIfNull(coordinator);
        }

        public string Name => "cycle_probe";

        public string Description => "test probe";

        public string ParametersSchema => "{}";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException("test probe tool is never executed");
    }

    [Fact]
    public void Composing_the_agent_DI_graph_does_not_cycle_between_coordinator_and_toolRegistry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sahost-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(Substitute.For<ILlmClient>());
            services.AddSingleton(Substitute.For<ISubagentExecutor>());
            services.AddSingleton(new ActiveChannelStore());
            services.AddSingleton(new AgentMessageChannel());
            services.AddSingleton(_ => new SubagentSessionStore(dir, NullLogger<SubagentSessionStore>.Instance));
            services.AddSingleton(_ => new SubagentRunnerRegistry(5, NullLogger<SubagentRunnerRegistry>.Instance));

            // Mirror Program.cs's FIXED coordinator registration: the runner factory resolves ToolRegistry
            // LAZILY (only when a subagent is actually dispatched), never during coordinator construction.
            // If this is ever changed back to an eager `sp.GetRequiredService<ToolRegistry>()` in the
            // factory body, MS.DI detects the construction cycle below and this test fails.
            services.AddSingleton(sp =>
            {
                Func<SubagentTask, SubagentRunner> runnerFactory = _ => new SubagentRunner(
                    sp.GetRequiredService<ILlmClient>(),
                    sp.GetRequiredService<ToolRegistry>(),
                    10,
                    NullLogger<SubagentRunner>.Instance);

                return new SubagentExecutionCoordinator(
                    sp.GetRequiredService<SubagentSessionStore>(),
                    sp.GetRequiredService<SubagentRunnerRegistry>(),
                    sp.GetRequiredService<ISubagentExecutor>(),
                    runnerFactory,
                    sp.GetRequiredService<AgentMessageChannel>(),
                    NullLogger<SubagentExecutionCoordinator>.Instance);
            });

            // The tool that depends on the coordinator, aggregated into ToolRegistry — the cycle edge.
            services.AddSingleton<IAgentTool>(sp =>
                new CoordinatorDependentProbeTool(sp.GetRequiredService<SubagentExecutionCoordinator>()));
            services.AddSingleton(sp =>
                new ToolRegistry(sp.GetServices<IAgentTool>(), sp.GetRequiredService<ActiveChannelStore>(), NullLogger<ToolRegistry>.Instance));

            using var provider = services.BuildServiceProvider();

            // With the eager (buggy) registration these throw InvalidOperationException (circular
            // dependency); with the lazy fix they resolve cleanly.
            var toolRegistry = provider.GetRequiredService<ToolRegistry>();
            var coordinator = provider.GetRequiredService<SubagentExecutionCoordinator>();

            Assert.NotNull(toolRegistry);
            Assert.NotNull(coordinator);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
