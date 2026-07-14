using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpHostServiceTests
{
    private static IMcpServerConnection ErroredConnection(string key)
    {
        var connection = Substitute.For<IMcpServerConnection>();
        connection.ServerKey.Returns(key);
        connection.Status.Returns(McpServerStatus.Error);
        connection.Tools.Returns([]);
        connection.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return connection;
    }

    private static McpToolDefinition Def(string serverKey, string tool) => new()
    {
        ServerKey = serverKey,
        ToolName = tool,
        FullName = McpToolNamer.Full(serverKey, tool),
        Description = $"{tool} desc",
        ParametersSchemaJson = "{}",
    };

    private static IMcpServerConnection FakeConnection(string key, params McpToolDefinition[] tools)
    {
        var connection = Substitute.For<IMcpServerConnection>();
        connection.ServerKey.Returns(key);
        connection.Status.Returns(McpServerStatus.Connected);
        connection.Tools.Returns(tools);
        connection.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return connection;
    }

    private static McpServerConfig StdioServer(string key, bool enabled = true) => new()
    {
        Key = key,
        Enabled = enabled,
        Transport = McpTransport.Stdio,
        Command = "node",
    };

    private static McpToolInvocation Invocation(string serverKey, string tool, string argumentsJson = "{}") => new()
    {
        InvocationId = Guid.CreateVersion7().ToString("N"),
        ServerKey = serverKey,
        ToolName = tool,
        ArgumentsJson = argumentsJson,
    };

    [Fact]
    public async Task ReconcileAsync_TwoEnabledServers_AggregatesCatalog()
    {
        var connectionA = FakeConnection("a", Def("a", "x"));
        var connectionB = FakeConnection("b", Def("b", "y"), Def("b", "z"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Is<McpServerConfig>(s => s.Key == "a")).Returns(connectionA);
        factory.TryCreate(Arg.Is<McpServerConfig>(s => s.Key == "b")).Returns(connectionB);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        var settings = new McpSettingsConfig { Enabled = true, Servers = [StdioServer("a"), StdioServer("b")] };
        await host.ReconcileAsync(settings, CancellationToken.None);

        var names = host.CurrentCatalog.Tools.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(["mcp__a__x", "mcp__b__y", "mcp__b__z"], names);
    }

    [Fact]
    public async Task ReconcileAsync_MasterDisabled_ProducesEmptyCatalog()
    {
        var connection = FakeConnection("a", Def("a", "x"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(connection);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        await host.ReconcileAsync(new McpSettingsConfig { Enabled = true, Servers = [StdioServer("a")] }, CancellationToken.None);
        Assert.Single(host.CurrentCatalog.Tools);

        await host.ReconcileAsync(new McpSettingsConfig { Enabled = false, Servers = [StdioServer("a")] }, CancellationToken.None);

        Assert.Empty(host.CurrentCatalog.Tools);
        await connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ReconcileAsync_ServerDisabled_StopsAndRemovesItsTools()
    {
        var connection = FakeConnection("a", Def("a", "x"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(connection);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        await host.ReconcileAsync(new McpSettingsConfig { Servers = [StdioServer("a")] }, CancellationToken.None);
        await host.ReconcileAsync(new McpSettingsConfig { Servers = [StdioServer("a", enabled: false)] }, CancellationToken.None);

        Assert.Empty(host.CurrentCatalog.Tools);
        await connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ReconcileAsync_AlreadyRunning_DoesNotRecreate()
    {
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(_ => FakeConnection("a", Def("a", "x")));
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        var settings = new McpSettingsConfig { Servers = [StdioServer("a")] };
        await host.ReconcileAsync(settings, CancellationToken.None);
        await host.ReconcileAsync(settings, CancellationToken.None);

        factory.Received(1).TryCreate(Arg.Any<McpServerConfig>());
    }

    [Fact]
    public async Task ReconcileAsync_RaisesCatalogChanged()
    {
        var connection = FakeConnection("a", Def("a", "x"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(connection);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);
        McpToolCatalog? pushed = null;
        host.CatalogChanged += (catalog, _) =>
        {
            pushed = catalog;
            return Task.CompletedTask;
        };

        await host.ReconcileAsync(new McpSettingsConfig { Servers = [StdioServer("a")] }, CancellationToken.None);

        Assert.NotNull(pushed);
        Assert.Single(pushed!.Tools);
    }

    [Fact]
    public async Task InvokeAsync_RoutesToOwningConnection()
    {
        var connection = FakeConnection("a", Def("a", "x"));
        connection.CallToolAsync(Arg.Any<McpToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => McpToolResult.Ok(callInfo.Arg<McpToolInvocation>().InvocationId, "result"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(connection);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);
        await host.ReconcileAsync(new McpSettingsConfig { Servers = [StdioServer("a")] }, CancellationToken.None);

        var invocation = Invocation("a", "x");
        var result = await host.InvokeAsync(invocation, CancellationToken.None);

        Assert.Equal(McpToolOutcome.Succeeded, result.Outcome);
        Assert.Equal(invocation.InvocationId, result.InvocationId);
        Assert.Equal("result", result.Content);
        await connection.Received(1).CallToolAsync(
            Arg.Is<McpToolInvocation>(i => i.InvocationId == invocation.InvocationId && i.ToolName == "x"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_UnknownServer_ReturnsDefinitiveFailure()
    {
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        var invocation = Invocation("missing", "x");
        var result = await host.InvokeAsync(invocation, CancellationToken.None);

        // Pre-dispatch unavailability is a definitive failure, never an unknown outcome.
        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Unavailable, result.FailureKind);
        Assert.Equal(invocation.InvocationId, result.InvocationId);
        Assert.Contains("not available", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconcile_AfterTransportFailure_RecreatesConnection()
    {
        var firstStatus = McpServerStatus.Connected;
        var firstTools = new[] { Def("a", "x") };
        var first = FakeConnection("a");
        first.Status.Returns(_ => firstStatus);
        first.Tools.Returns(_ => firstTools);
        first.CallToolAsync(Arg.Any<McpToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // Simulate a fatal transport closure mid-call: the connection clears its
                // tools, moves to Error, and reports the ambiguous outcome.
                firstStatus = McpServerStatus.Error;
                firstTools = [];
                return McpToolResult.Unknown(
                    callInfo.Arg<McpToolInvocation>().InvocationId, McpFailureKind.Transport, "transport lost");
            });
        var second = FakeConnection("a", Def("a", "x"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(first, second);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);
        var settings = new McpSettingsConfig { Servers = [StdioServer("a")] };
        await host.ReconcileAsync(settings, CancellationToken.None);
        Assert.Single(host.CurrentCatalog.Tools);

        var result = await host.InvokeAsync(Invocation("a", "x"), CancellationToken.None);

        // The dead server's tools disappear immediately, without waiting for the next reconcile.
        Assert.Equal(McpToolOutcome.OutcomeUnknown, result.Outcome);
        Assert.Empty(host.CurrentCatalog.Tools);

        // The existing periodic reconciliation recreates the errored connection.
        await host.ReconcileAsync(settings, CancellationToken.None);

        Assert.Single(host.CurrentCatalog.Tools);
        await first.Received(1).DisposeAsync();
        factory.Received(2).TryCreate(Arg.Any<McpServerConfig>());
    }

    [Fact]
    public async Task OriginalInvocation_IsNeverDispatchedTwice()
    {
        var firstStatus = McpServerStatus.Connected;
        var firstTools = new[] { Def("a", "x") };
        var first = FakeConnection("a");
        first.Status.Returns(_ => firstStatus);
        first.Tools.Returns(_ => firstTools);
        first.CallToolAsync(Arg.Any<McpToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                firstStatus = McpServerStatus.Error;
                firstTools = [];
                return McpToolResult.Unknown(
                    callInfo.Arg<McpToolInvocation>().InvocationId, McpFailureKind.Transport, "transport lost");
            });
        var second = FakeConnection("a", Def("a", "x"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(first, second);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);
        var settings = new McpSettingsConfig { Servers = [StdioServer("a")] };
        await host.ReconcileAsync(settings, CancellationToken.None);

        var invocation = Invocation("a", "x");
        var result = await host.InvokeAsync(invocation, CancellationToken.None);
        await host.ReconcileAsync(settings, CancellationToken.None); // recovery reconnect

        // CRITICAL: the ambiguous invocation surfaces as OutcomeUnknown and is NEVER replayed —
        // not on the failed connection, and not on its replacement.
        Assert.Equal(McpToolOutcome.OutcomeUnknown, result.Outcome);
        await first.Received(1).CallToolAsync(Arg.Any<McpToolInvocation>(), Arg.Any<CancellationToken>());
        await second.DidNotReceive().CallToolAsync(Arg.Any<McpToolInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_ServerErrorsThenSucceeds_EndsConnectedAfterBackoff()
    {
        var time = new FakeTimeProvider();
        var errored = ErroredConnection("a");
        var healthy = FakeConnection("a", Def("a", "x"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(errored, healthy);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance, time);

        var settings = new McpSettingsConfig { Servers = [StdioServer("a")] };

        await host.ReconcileAsync(settings, CancellationToken.None);
        Assert.Empty(host.CurrentCatalog.Tools);

        time.Advance(TimeSpan.FromSeconds(5));
        await host.ReconcileAsync(settings, CancellationToken.None);

        Assert.Single(host.CurrentCatalog.Tools);
        await errored.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ReconcileAsync_ErroredServerWithinBackoffWindow_NotRetried()
    {
        var time = new FakeTimeProvider();
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(_ => ErroredConnection("a"));
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance, time);

        var settings = new McpSettingsConfig { Servers = [StdioServer("a")] };
        await host.ReconcileAsync(settings, CancellationToken.None);

        // No time advance: still inside the first backoff window, so no second create attempt.
        await host.ReconcileAsync(settings, CancellationToken.None);

        factory.Received(1).TryCreate(Arg.Any<McpServerConfig>());
    }

    [Fact]
    public async Task ReconcileAsync_PushRunsOutsideLock_NestedReconcileDoesNotDeadlock()
    {
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(_ => FakeConnection("a", Def("a", "x")));
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        var nested = false;
        host.CatalogChanged += async (_, ct) =>
        {
            if (nested)
            {
                return;
            }

            nested = true;
            // If the push were awaited while holding the reconcile lock, this nested reconcile
            // would block forever. A timeout converts a regression into a fast failure.
            await host.ReconcileAsync(new McpSettingsConfig { Servers = [StdioServer("a")] }, ct)
                .WaitAsync(TimeSpan.FromSeconds(5), ct);
        };

        await host.ReconcileAsync(new McpSettingsConfig { Servers = [StdioServer("a")] }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(nested);
    }

    [Fact]
    public async Task ReconcileAsync_DisablingOneServer_RepublishesCatalogWithoutItsToolsLive()
    {
        var connectionA = FakeConnection("a", Def("a", "x"));
        var connectionB = FakeConnection("b", Def("b", "y"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Is<McpServerConfig>(s => s.Key == "a")).Returns(connectionA);
        factory.TryCreate(Arg.Is<McpServerConfig>(s => s.Key == "b")).Returns(connectionB);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        McpToolCatalog? lastPush = null;
        host.CatalogChanged += (catalog, _) =>
        {
            lastPush = catalog;
            return Task.CompletedTask;
        };

        await host.ReconcileAsync(
            new McpSettingsConfig { Servers = [StdioServer("a"), StdioServer("b")] }, CancellationToken.None);

        // Disable server 'a' (master still on) — tools must drop live, no full restart of 'b'.
        await host.ReconcileAsync(
            new McpSettingsConfig { Servers = [StdioServer("a", enabled: false), StdioServer("b")] },
            CancellationToken.None);

        Assert.NotNull(lastPush);
        Assert.Equal(["mcp__b__y"], lastPush!.Tools.Select(t => t.FullName).ToList());
        await connectionA.Received(1).DisposeAsync();
        await connectionB.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task ReconcileAsync_UnchangedCatalog_DoesNotRepush()
    {
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(_ => FakeConnection("a", Def("a", "x")));
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        var pushes = 0;
        host.CatalogChanged += (_, _) =>
        {
            pushes++;
            return Task.CompletedTask;
        };

        var settings = new McpSettingsConfig { Servers = [StdioServer("a")] };
        await host.ReconcileAsync(settings, CancellationToken.None);
        await host.ReconcileAsync(settings, CancellationToken.None);

        Assert.Equal(1, pushes);
    }

    [Fact]
    public async Task ForceReconnectAsync_DisposesAndRecreatesConnection()
    {
        var first = FakeConnection("a", Def("a", "old"));
        var second = FakeConnection("a", Def("a", "new"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(first, second);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        var settings = new McpSettingsConfig { Servers = [StdioServer("a")] };
        await host.ReconcileAsync(settings, CancellationToken.None);
        Assert.Equal(["mcp__a__old"], host.CurrentCatalog.Tools.Select(t => t.FullName).ToList());

        await host.ForceReconnectAsync("a", settings, CancellationToken.None);

        await first.Received(1).DisposeAsync();
        Assert.Equal(["mcp__a__new"], host.CurrentCatalog.Tools.Select(t => t.FullName).ToList());
    }
}
