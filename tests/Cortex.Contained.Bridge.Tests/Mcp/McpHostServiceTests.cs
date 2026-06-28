using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpHostServiceTests
{
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
        connection.CallToolAsync("x", "{}", Arg.Any<CancellationToken>()).Returns(McpToolResult.Ok("result"));
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        factory.TryCreate(Arg.Any<McpServerConfig>()).Returns(connection);
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);
        await host.ReconcileAsync(new McpSettingsConfig { Servers = [StdioServer("a")] }, CancellationToken.None);

        var result = await host.InvokeAsync("a", "x", "{}", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("result", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_UnknownServer_ReturnsFailure()
    {
        var factory = Substitute.For<IMcpServerConnectionFactory>();
        await using var host = new McpHostService(factory, NullLogger<McpHostService>.Instance);

        var result = await host.InvokeAsync("missing", "x", "{}", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not available", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
