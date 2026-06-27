using System.Reflection;
using System.Text.Json.Nodes;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Cortex.Contained.Bridge.Tests.Coding.FakeCoda;

/// <summary>
/// An in-memory JSON-RPC server that emulates <c>coda serve</c>.
/// Answers <c>initialize</c> and executes scripted <c>session/prompt</c> behaviours.
/// </summary>
public sealed class FakeCodaServer : IAsyncDisposable
{
    private static readonly string[] questionOptions = ["A", "B"];

    private readonly JsonRpc serverRpc;
    private readonly Stream serverStream;

    /// <summary>
    /// Selects which behaviour to use when the next <c>session/prompt</c> arrives.
    /// </summary>
    public FakeCodaScenario Scenario { get; set; } = FakeCodaScenario.Happy;

    /// <summary>When true, the server never replies to <c>initialize</c>.</summary>
    public bool HangInitialize { get; set; }

    /// <summary>When set, <c>initialize</c> fails with a JSON-RPC error carrying this message
    /// (emulates coda rejecting an unusable provider/model instead of hanging).</summary>
    public string? FailInitializeMessage { get; set; }

    /// <summary>When set, <c>initialize</c> returns this <c>telemetryLogPath</c> in its result.</summary>
    public string? TelemetryLogPath { get; set; }

    /// <summary>The <c>sessionId</c> the client sent on <c>initialize</c> (null when omitted).</summary>
    public string? ReceivedInitializeSessionId { get; private set; }

    /// <summary>True once an <c>initialize</c> request has been handled.</summary>
    public bool ReceivedInitialize { get; private set; }

    /// <summary>The goal config from the last <c>session/setGoal</c> call (null if none).</summary>
    public (string? Goal, string? MaxDuration, int? MaxContinuations)? LastSetGoal { get; private set; }

    /// <summary>Steering comments received via <c>session/steer</c>, in arrival order.</summary>
    public List<string> SteerComments { get; } = [];

    /// <summary>Text to accumulate and include in the final <c>event/assistantText</c> notification.</summary>
    public string AssistantText { get; set; } = "Hello from fake coda.";

    /// <summary>
    /// The transcript returned by <c>session/history</c> / <c>session/messages</c>.
    /// Defaults to a small user/assistant exchange; tests may replace it.
    /// </summary>
    public IReadOnlyList<(string Role, string Content)> HistoryMessages { get; set; } =
    [
        ("user", "hello"),
        ("assistant", "hi there"),
        ("user", "do a thing"),
    ];

    /// <summary>Creates a server wired to <paramref name="serverStream"/>.</summary>
    private FakeCodaServer(Stream serverStream)
    {
        this.serverStream = serverStream;

        var formatter = new SystemTextJsonFormatter();
        this.serverRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(serverStream, serverStream, formatter));

        // initialize: registered via a real instance method whose sessionId param is OPTIONAL,
        // so StreamJsonRpc binds an omitted (new-session) named param to null. Delegates do not
        // reliably expose optional/default params to StreamJsonRpc's binder, hence the MethodInfo.
        this.serverRpc.AddLocalRpcMethod(
            "initialize",
            typeof(FakeCodaServer).GetMethod(nameof(this.InitializeRpcAsync), BindingFlags.Instance | BindingFlags.NonPublic)!,
            this);

        // session/prompt: dispatch to the selected scenario.
        this.serverRpc.AddLocalRpcMethod(
            "session/prompt",
            new Func<string, CancellationToken, Task<JsonNode?>>(this.HandlePromptAsync));

        // session/history: returns the scripted transcript. Registered via a MethodInfo with an
        // OPTIONAL param so the client's empty parameter object (`{}` → zero named args) binds
        // without StreamJsonRpc demanding a positional argument (same reason as initialize).
        this.serverRpc.AddLocalRpcMethod(
            "session/history",
            typeof(FakeCodaServer).GetMethod(nameof(this.HistoryRpc), BindingFlags.Instance | BindingFlags.NonPublic)!,
            this);

        // session/messages: returns the slice after sinceIndex plus the next cursor.
        this.serverRpc.AddLocalRpcMethod(
            "session/messages",
            new Func<int, JsonNode?>(sinceIndex => new JsonObject
            {
                ["messages"] = this.BuildMessages(sinceIndex),
                ["nextIndex"] = this.HistoryMessages.Count,
            }));

        // session/interrupt: no-op.
        this.serverRpc.AddLocalRpcMethod(
            "session/interrupt",
            new Func<JsonNode?, Task>(_ => Task.CompletedTask));

        // session/steer: record the steering comment so tests can assert it was delivered.
        this.serverRpc.AddLocalRpcMethod(
            "session/steer",
            new Func<string, JsonNode?>(text =>
            {
                this.SteerComments.Add(text);
                return new JsonObject { ["ok"] = true };
            }));

        // session/setGoal: echo the supplied goal config back (mirrors coda's SetGoalResult).
        this.serverRpc.AddLocalRpcMethod(
            "session/setGoal",
            new Func<string?, string?, int?, JsonNode?>((goal, maxDuration, maxContinuations) =>
            {
                this.LastSetGoal = (goal, maxDuration, maxContinuations);
                return new JsonObject
                {
                    ["ok"] = true,
                    ["goal"] = goal,
                    ["maxDuration"] = maxDuration,
                    ["maxContinuations"] = maxContinuations,
                };
            }));

        // shutdown: no-op.
        this.serverRpc.AddLocalRpcMethod(
            "shutdown",
            new Func<JsonNode?, Task>(_ => Task.CompletedTask));
    }

    /// <summary>
    /// Handles the <c>initialize</c> request. <paramref name="sessionId"/> is optional so an
    /// omitted (new-session) named param binds to null; a resume sends a concrete id.
    /// </summary>
    private async Task<JsonNode?> InitializeRpcAsync(string protocolVersion, string? sessionId = null, CancellationToken ct = default)
    {
        this.ReceivedInitialize = true;
        this.ReceivedInitializeSessionId = sessionId;
        if (this.FailInitializeMessage is { } failMessage)
        {
            throw new LocalRpcException(failMessage) { ErrorCode = -32010 };
        }

        if (this.HangInitialize)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        var result = new JsonObject
        {
            ["protocolVersion"] = "1",
            ["sessionId"] = sessionId ?? Guid.NewGuid().ToString("N"),
            ["serverInfo"] = "fake-coda",
        };

        if (this.TelemetryLogPath is { } telemetryLogPath)
        {
            result["telemetryLogPath"] = telemetryLogPath;
        }

        return result;
    }

    /// <summary>Creates a <see cref="FakeCodaServer"/> plus the client-side stream to connect to.</summary>
    public static (FakeCodaServer Server, Stream ClientStream) Create()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var server = new FakeCodaServer(serverStream);
        server.serverRpc.StartListening();
        return (server, clientStream);
    }

    // -----------------------------------------------------------------------
    // Prompt scenarios
    // -----------------------------------------------------------------------

    private async Task<JsonNode?> HandlePromptAsync(string text, CancellationToken ct)
    {
        return this.Scenario switch
        {
            FakeCodaScenario.Happy => await this.RunHappyAsync(ct),
            FakeCodaScenario.Permission => await this.RunPermissionAsync(ct),
            FakeCodaScenario.Question => await this.RunQuestionAsync(ct),
            FakeCodaScenario.Plan => await this.RunPlanAsync(ct),
            FakeCodaScenario.Crash => await this.RunCrashAsync(ct),
            FakeCodaScenario.Stall => await this.RunStallAsync(ct),
            FakeCodaScenario.LimitReached => await this.RunLimitReachedAsync(ct),
            FakeCodaScenario.SlowDrip => await this.RunSlowDripAsync(ct),
            FakeCodaScenario.SlowStream => await this.RunSlowStreamAsync(ct),
            FakeCodaScenario.Goal => await this.RunGoalAsync(ct),
            _ => await this.RunHappyAsync(ct),
        };
    }

    private async Task<JsonNode?> RunHappyAsync(CancellationToken ct)
    {
        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/assistantText",
            new { delta = this.AssistantText });

        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/toolCall",
            new { toolName = "Read", inputJson = "{\"path\":\"README.md\"}" });

        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/usage",
            new { inputTokens = 1234L, outputTokens = 567L });

        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/turnComplete",
            new { stopReason = "end_turn", interrupted = false });

        return new JsonObject { ["ok"] = true, ["stopReason"] = "end_turn", ["interrupted"] = false };
    }

    private async Task<JsonNode?> RunPermissionAsync(CancellationToken ct)
    {
        // Invoke request/permission on the client and wait for its reply.
        var reply = await this.serverRpc.InvokeWithParameterObjectAsync<JsonNode>(
            "request/permission",
            new { toolName = "Bash", inputPreview = "rm -rf /tmp/x" },
            ct);

        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/turnComplete",
            new { stopReason = "end_turn", interrupted = false });

        return new JsonObject { ["ok"] = true, ["stopReason"] = "end_turn", ["interrupted"] = false };
    }

    private async Task<JsonNode?> RunQuestionAsync(CancellationToken ct)
    {
        var reply = await this.serverRpc.InvokeWithParameterObjectAsync<JsonNode>(
            "request/question",
            new { question = "Which approach?", options = questionOptions, multiSelect = false },
            ct);

        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/turnComplete",
            new { stopReason = "end_turn", interrupted = false });

        return new JsonObject { ["ok"] = true, ["stopReason"] = "end_turn", ["interrupted"] = false };
    }

    private async Task<JsonNode?> RunPlanAsync(CancellationToken ct)
    {
        var reply = await this.serverRpc.InvokeWithParameterObjectAsync<JsonNode>(
            "request/planApproval",
            new { plan = "Step 1: do X\nStep 2: do Y" },
            ct);

        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/turnComplete",
            new { stopReason = "end_turn", interrupted = false });

        return new JsonObject { ["ok"] = true, ["stopReason"] = "end_turn", ["interrupted"] = false };
    }

    private async Task<JsonNode?> RunLimitReachedAsync(CancellationToken ct)
    {
        // Emit a recoverable limit then complete the turn (as coda does on a max_tokens / iteration cap).
        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/limitReached",
            new { kind = "max_tokens", message = "The response was truncated (max_tokens reached)." });
        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/turnComplete",
            new { stopReason = "max_tokens", interrupted = false });

        return new JsonObject { ["ok"] = true, ["stopReason"] = "max_tokens", ["interrupted"] = false };
    }

    private Task<JsonNode?> RunCrashAsync(CancellationToken ct)
    {
        // Close the server stream to simulate a connection drop mid-prompt.
        // Do this on a background thread so the JsonRpc layer can observe the disconnect.
        _ = Task.Run(async () =>
        {
            await Task.Yield();
            this.serverRpc.Dispose();
            await this.serverStream.DisposeAsync();
        }, ct);

        // Return a task that never completes — the server closes before replying.
        return new TaskCompletionSource<JsonNode?>().Task;
    }

    private async Task<JsonNode?> RunSlowDripAsync(CancellationToken ct)
    {
        // Emit a few assistantText notifications spaced well within the idle window
        // (~400ms each, vs a 1s window) so each one refreshes LastActivityAt before
        // the watchdog can declare the session frozen, then complete the turn.
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
            await this.serverRpc.NotifyWithParameterObjectAsync(
                "event/assistantText",
                new { delta = "." });
        }

        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/turnComplete",
            new { stopReason = "end_turn", interrupted = false });

        return new JsonObject { ["ok"] = true, ["stopReason"] = "end_turn", ["interrupted"] = false };
    }

    private async Task<JsonNode?> RunGoalAsync(CancellationToken ct)
    {
        // An autonomous-goal run: emit a normal turn, then return a prompt result that carries a
        // goalStatus object (as coda does when a goal was active).
        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/assistantText", new { delta = this.AssistantText });
        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/turnComplete", new { stopReason = "end_turn", interrupted = false });

        return new JsonObject
        {
            ["ok"] = true,
            ["stopReason"] = "end_turn",
            ["interrupted"] = false,
            ["goalStatus"] = new JsonObject
            {
                ["outcome"] = "Met",
                ["remaining"] = null,
                ["continuations"] = 3,
                ["elapsedSeconds"] = 42.5,
                ["escalated"] = false,
                ["extensionUsed"] = false,
            },
        };
    }

    private async Task<JsonNode?> RunSlowStreamAsync(CancellationToken ct)
    {
        // A turn whose only mid-flight liveness is the LLM stream pulse: emit
        // event/streamProgress (first-token, several progress, complete) spaced wider than the
        // idle window, with NO assistantText/usage/toolCall until the very end. This is exactly
        // the production case the watchdog used to be blind to (coda mid-LLM-call).
        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/streamProgress", new { phase = "first-token", chunks = 0, chars = 0, elapsedMs = 50L });

        for (var i = 1; i <= 4; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
            await this.serverRpc.NotifyWithParameterObjectAsync(
                "event/streamProgress", new { phase = "progress", chunks = i * 5, chars = i * 100, elapsedMs = (long)(i * 500) });
        }

        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/streamProgress", new { phase = "complete", chunks = 20, chars = 400, elapsedMs = 2200L });
        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/assistantText", new { delta = this.AssistantText });
        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/turnComplete", new { stopReason = "end_turn", interrupted = false });

        return new JsonObject { ["ok"] = true, ["stopReason"] = "end_turn", ["interrupted"] = false };
    }

#pragma warning disable CA1822 // Member does not access instance data; kept instance for switch-dispatch symmetry.
    private async Task<JsonNode?> RunStallAsync(CancellationToken ct)
    {
        // Accept the prompt but emit no events and never complete — emulates a coda
        // that has gone silent mid-turn.
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return null;
    }
#pragma warning restore CA1822

    /// <summary>
    /// Handles <c>session/history</c>. The <c>ignored</c> param is optional so an empty
    /// parameter object binds it to null (mirrors the initialize registration).
    /// </summary>
    private JsonObject HistoryRpc(JsonNode? ignored = null)
    {
        return new JsonObject { ["messages"] = this.BuildMessages(0) };
    }

    /// <summary>Builds the <c>messages</c> array for the transcript slice starting at <paramref name="from"/>.</summary>
    private JsonArray BuildMessages(int from)
    {
        var array = new JsonArray();
        for (var i = Math.Max(0, from); i < this.HistoryMessages.Count; i++)
        {
            var (role, content) = this.HistoryMessages[i];
            array.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }

        return array;
    }

    public async ValueTask DisposeAsync()
    {
        this.serverRpc.Dispose();
        await this.serverStream.DisposeAsync();
    }
}

/// <summary>Scripted behaviour for <see cref="FakeCodaServer"/>.</summary>
public enum FakeCodaScenario
{
    Happy,
    Permission,
    Question,
    Plan,
    Crash,
    Stall,
    LimitReached,
    SlowDrip,
    SlowStream,
    Goal,
}
