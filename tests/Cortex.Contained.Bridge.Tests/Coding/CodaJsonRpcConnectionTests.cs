using System.Text.Json.Nodes;
using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Bridge.Tests.Coding.FakeCoda;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodaJsonRpcConnectionTests
{
    [Fact]
    public async Task InitializeAsync_returns_session_id_from_server()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        // Fake coda server: answers "initialize" with a sessionId.
        // InvokeWithParameterObjectAsync sends named params; the server handler must match
        // the individual named fields (protocolVersion, sessionId).
        var formatter = new SystemTextJsonFormatter();
        var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, formatter));
        serverRpc.AddLocalRpcMethod("initialize", new Func<string, string, JsonNode?>(
            (protocolVersion, sessionId) =>
                new JsonObject { ["protocolVersion"] = "1", ["sessionId"] = "srv-123", ["serverInfo"] = "coda" }));
        serverRpc.StartListening();

        await using var conn = new CodaJsonRpcConnection(clientStream, clientStream);
        conn.Start();

        var outcome = await conn.InitializeAsync("want-this", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("srv-123", outcome.SessionId);
        Assert.Null(outcome.TelemetryLogPath);
        serverRpc.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_returns_telemetry_log_path_from_server()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var formatter = new SystemTextJsonFormatter();
        var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, formatter));
        serverRpc.AddLocalRpcMethod("initialize", new Func<string, string, JsonNode?>(
            (protocolVersion, sessionId) =>
                new JsonObject
                {
                    ["protocolVersion"] = "1",
                    ["sessionId"] = "srv-123",
                    ["serverInfo"] = "coda",
                    ["telemetryLogPath"] = "/tmp/coda/telemetry-abc.log",
                }));
        serverRpc.StartListening();

        await using var conn = new CodaJsonRpcConnection(clientStream, clientStream);
        conn.Start();

        var outcome = await conn.InitializeAsync("want-this", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("srv-123", outcome.SessionId);
        Assert.Equal("/tmp/coda/telemetry-abc.log", outcome.TelemetryLogPath);
        serverRpc.Dispose();
    }

    [Fact]
    public async Task Usage_event_fires_with_parsed_totals()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var formatter = new SystemTextJsonFormatter();
        var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, formatter));
        serverRpc.StartListening();

        await using var conn = new CodaJsonRpcConnection(clientStream, clientStream);

        var usageSignal = new TaskCompletionSource<UsageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Usage += dto => usageSignal.TrySetResult(dto);

        conn.Start();

        await serverRpc.NotifyWithParameterObjectAsync(
            "event/usage",
            new { inputTokens = 1234L, outputTokens = 567L });

        var usage = await usageSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1234L, usage.InputTokens);
        Assert.Equal(567L, usage.OutputTokens);
        serverRpc.Dispose();
    }

    [Fact]
    public async Task StreamProgress_Notification_RaisesEventWithFields()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var formatter = new SystemTextJsonFormatter();
        var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, formatter));
        serverRpc.StartListening();

        await using var conn = new CodaJsonRpcConnection(clientStream, clientStream);

        var got = new TaskCompletionSource<StreamProgressDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.StreamProgress += dto => got.TrySetResult(dto);

        conn.Start();

        await serverRpc.NotifyWithParameterObjectAsync(
            "event/streamProgress",
            new { phase = "progress", chunks = 12, chars = 480, elapsedMs = 2100L });

        var dto = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("progress", dto.Phase);
        Assert.Equal(12, dto.Chunks);
        Assert.Equal(480, dto.Chars);
        Assert.Equal(2100L, dto.ElapsedMs);
        serverRpc.Dispose();
    }

    [Fact]
    public async Task ToolProgress_Notification_RaisesEventWithFields()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var formatter = new SystemTextJsonFormatter();
        var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, formatter));
        serverRpc.StartListening();

        await using var conn = new CodaJsonRpcConnection(clientStream, clientStream);

        var got = new TaskCompletionSource<ToolProgressDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.ToolProgress += dto => got.TrySetResult(dto);

        conn.Start();

        await serverRpc.NotifyWithParameterObjectAsync(
            "event/toolProgress",
            new { toolName = "run_command", elapsedMs = 42_000L });

        var dto = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("run_command", dto.ToolName);
        Assert.Equal(42_000L, dto.ElapsedMs);
        serverRpc.Dispose();
    }

    [Fact]
    public async Task SetGoalAsync_returns_echoed_goal_config()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var formatter = new SystemTextJsonFormatter();
        var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, formatter));
        serverRpc.AddLocalRpcMethod("session/setGoal", new Func<string?, string?, int?, JsonNode?>(
            (goal, maxDuration, maxContinuations) => new JsonObject
            {
                ["ok"] = true,
                ["goal"] = goal,
                ["maxDuration"] = maxDuration,
                ["maxContinuations"] = maxContinuations,
            }));
        serverRpc.StartListening();

        await using var conn = new CodaJsonRpcConnection(clientStream, clientStream);
        conn.Start();

        var result = await conn.SetGoalAsync("all tests pass", "30m", 200, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Ok);
        Assert.Equal("all tests pass", result.Goal);
        Assert.Equal("30m", result.MaxDuration);
        Assert.Equal(200, result.MaxContinuations);
        serverRpc.Dispose();
    }

    [Fact]
    public async Task HistoryAsync_returns_role_content_list()
    {
        var (server, clientStream) = FakeCodaServer.Create();
        await using var _ = server;
        server.HistoryMessages =
        [
            ("user", "first"),
            ("assistant", "second"),
        ];

        await using var conn = new CodaJsonRpcConnection(clientStream, clientStream);
        conn.Start();

        var result = await conn.HistoryAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Equal("first", result.Messages[0].Content);
        Assert.Equal("assistant", result.Messages[1].Role);
        Assert.Equal("second", result.Messages[1].Content);
    }

    [Fact]
    public async Task MessagesAsync_returns_slice_and_next_index()
    {
        var (server, clientStream) = FakeCodaServer.Create();
        await using var _ = server;
        server.HistoryMessages =
        [
            ("user", "m0"),
            ("assistant", "m1"),
            ("user", "m2"),
            ("assistant", "m3"),
        ];

        await using var conn = new CodaJsonRpcConnection(clientStream, clientStream);
        conn.Start();

        var result = await conn.MessagesAsync(2, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("m2", result.Messages[0].Content);
        Assert.Equal("m3", result.Messages[1].Content);
        Assert.Equal(4, result.NextIndex);
    }
}
