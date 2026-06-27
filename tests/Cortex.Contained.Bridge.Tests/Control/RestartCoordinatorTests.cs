using Cortex.Contained.Bridge.Control;

namespace Cortex.Contained.Bridge.Tests.Control;

/// <summary>
/// <see cref="RestartCoordinator"/> is the small piece of shared state between
/// the <c>POST /api/control/restart-bridge</c> endpoint and the
/// <c>ApplicationStopped</c> lifetime callback: it remembers whether the
/// graceful shutdown that's about to happen was triggered by a Web-UI restart
/// request (→ exit code 73, Launcher respawns) or by a normal stop
/// (→ exit code 0, no respawn).
/// </summary>
public class RestartCoordinatorTests
{
    [Fact]
    public void DefaultState_IsNotRequested()
    {
        var c = new RestartCoordinator();

        Assert.False(c.IsRestartRequested);
        Assert.Equal(0, c.ResolveExitCode());
    }

    [Fact]
    public void RequestRestart_FlipsFlagAndExitCode()
    {
        var c = new RestartCoordinator();

        c.RequestRestart();

        Assert.True(c.IsRestartRequested);
        Assert.Equal(RestartCoordinator.RestartExitCode, c.ResolveExitCode());
        Assert.Equal(73, RestartCoordinator.RestartExitCode); // pin the wire value
    }

    [Fact]
    public void RequestRestart_IsIdempotent()
    {
        var c = new RestartCoordinator();

        c.RequestRestart();
        c.RequestRestart();
        c.RequestRestart();

        Assert.True(c.IsRestartRequested);
        Assert.Equal(73, c.ResolveExitCode());
    }
}
