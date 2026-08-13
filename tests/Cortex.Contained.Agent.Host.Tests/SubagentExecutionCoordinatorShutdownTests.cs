using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Covers coordinator shutdown when stop and disposal interleave.
/// <para>
/// A hosted service does not get to assume that <see cref="IHostedService.StopAsync"/> runs before
/// <see cref="IDisposable.Dispose"/>, or that either runs once. Under
/// <c>WebApplicationFactory</c> teardown they overlap, and <c>StopAsync</c> called
/// <c>Cancel()</c> on a source <c>Dispose</c> had already disposed. The resulting
/// <see cref="ObjectDisposedException"/> propagated out of <c>Host.StopAsync</c>, which aborts the
/// remaining shutdown steps — so in-flight runners were left uncancelled and pending work
/// un-drained. In the integration suite it also failed the run with a non-zero exit code while
/// every test still reported as passed, which is how it stayed invisible.
/// </para>
/// </summary>
public class SubagentExecutionCoordinatorShutdownTests
{
    private static SubagentExecutionCoordinator NewCoordinator(string tempDir)
    {
        var store = new SubagentSessionStore(tempDir, NullLogger<SubagentSessionStore>.Instance);
        var registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);
        var executor = Substitute.For<ISubagentExecutor>();

        return new SubagentExecutionCoordinator(
            store,
            registry,
            executor,
            _ => new SubagentRunner(
                Substitute.For<ILlmClient>(),
                new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance),
                10,
                NullLogger<SubagentRunner>.Instance),
            new AgentMessageChannel(),
            NullLogger<SubagentExecutionCoordinator>.Instance,
            TimeSpan.FromSeconds(30));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "subagent-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task StopAsync_AfterDispose_DoesNotThrow()
    {
        // The container disposing the singleton before the host finishes stopping it is a real
        // ordering, not a contrived one — this is exactly what WebApplicationFactory teardown does.
        var dir = NewTempDir();
        try
        {
            var coordinator = NewCoordinator(dir);
            await coordinator.StartAsync(CancellationToken.None);

            coordinator.Dispose();

            await coordinator.StopAsync(CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StopAsync_CalledTwice_DoesNotThrow()
    {
        var dir = NewTempDir();
        try
        {
            var coordinator = NewCoordinator(dir);
            await coordinator.StartAsync(CancellationToken.None);

            await coordinator.StopAsync(CancellationToken.None);
            await coordinator.StopAsync(CancellationToken.None);

            coordinator.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        var dir = NewTempDir();
        try
        {
            var coordinator = NewCoordinator(dir);
            await coordinator.StartAsync(CancellationToken.None);
            await coordinator.StopAsync(CancellationToken.None);

            coordinator.Dispose();
            coordinator.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        // Shutdown can run after a startup failure, before StartAsync ever created the source.
        var dir = NewTempDir();
        try
        {
            var coordinator = NewCoordinator(dir);

            await coordinator.StopAsync(CancellationToken.None);
            coordinator.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
