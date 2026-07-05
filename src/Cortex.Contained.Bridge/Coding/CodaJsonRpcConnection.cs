using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace Cortex.Contained.Bridge.Coding;

// ---------------------------------------------------------------------------
// Wire DTOs — camelCase names match coda serve's JSON-RPC protocol exactly.
// ---------------------------------------------------------------------------

/// <summary>Result of the <c>initialize</c> request.</summary>
internal sealed record InitializeResultDto(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("serverInfo")] string ServerInfo,
    [property: JsonPropertyName("telemetryLogPath")] string? TelemetryLogPath = null);

/// <summary>The session details surfaced from a successful <c>initialize</c>.</summary>
public sealed record InitializeOutcome(string SessionId, string? TelemetryLogPath);

/// <summary>
/// Wire shape of coda's <c>goalStatus</c> object (present in the <c>session/prompt</c> result
/// when an autonomous goal was active and produced a non-<c>None</c> outcome).
/// </summary>
public sealed record GoalStatusDto(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("remaining")] string? Remaining,
    [property: JsonPropertyName("continuations")] int Continuations,
    [property: JsonPropertyName("elapsedSeconds")] double ElapsedSeconds,
    [property: JsonPropertyName("escalated")] bool Escalated,
    [property: JsonPropertyName("extensionUsed")] bool ExtensionUsed);

/// <summary>Result of the <c>session/prompt</c> request.</summary>
public sealed record PromptResultDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("stopReason")] string? StopReason,
    [property: JsonPropertyName("interrupted")] bool Interrupted,
    [property: JsonPropertyName("goalStatus")] GoalStatusDto? GoalStatus = null);

/// <summary>Result of the <c>session/setGoal</c> request (the goal config after the mutation).</summary>
public sealed record SetGoalResultDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("goal")] string? Goal,
    [property: JsonPropertyName("maxDuration")] string? MaxDuration,
    [property: JsonPropertyName("maxContinuations")] int? MaxContinuations);

/// <summary>Payload of the <c>event/turnComplete</c> notification.</summary>
public sealed record TurnCompleteDto(
    [property: JsonPropertyName("stopReason")] string? StopReason,
    [property: JsonPropertyName("interrupted")] bool Interrupted);

/// <summary>Payload of the <c>event/error</c> notification.</summary>
public sealed record ErrorDto(
    [property: JsonPropertyName("message")] string Message);

/// <summary>Result of the <c>session/steer</c> request — <c>ok</c> is true when a running turn accepted the comment.</summary>
internal sealed record SteerResultDto(
    [property: JsonPropertyName("ok")] bool Ok);

/// <summary>
/// Payload of the <c>event/limitReached</c> notification — a recoverable per-turn limit (output
/// <c>max_tokens</c> or the tool-iteration backstop). NOT a crash.
/// </summary>
public sealed record LimitReachedDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Payload of the <c>event/toolCall</c> notification.</summary>
public sealed record ToolCallDto(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("inputJson")] string InputJson);

/// <summary>Payload of the <c>event/toolResult</c> notification.</summary>
public sealed record ToolResultDto(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("outputJson")] string? OutputJson);

/// <summary>Payload of the <c>event/assistantText</c> notification.</summary>
public sealed record AssistantTextDto(
    [property: JsonPropertyName("delta")] string Delta);

/// <summary>Payload of the <c>event/usage</c> notification.</summary>
public sealed record UsageDto(
    [property: JsonPropertyName("inputTokens")] long InputTokens,
    [property: JsonPropertyName("outputTokens")] long OutputTokens);

/// <summary>
/// Payload of the <c>event/streamProgress</c> notification — coda's LLM-stream liveness pulse.
/// <c>phase</c> is <c>"first-token"</c> | <c>"progress"</c> | <c>"complete"</c>.
/// </summary>
public sealed record StreamProgressDto(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("chunks")] int Chunks,
    [property: JsonPropertyName("chars")] int Chars,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs);

/// <summary>
/// Payload of the <c>event/toolProgress</c> notification — coda's tool-execution liveness
/// pulse (the counterpart to <see cref="StreamProgressDto"/> for the tool phase). Consumed
/// as liveness so a long-running tool never trips the idle watchdog. <c>elapsedMs</c> is how
/// long the tool has been running so far.
/// </summary>
public sealed record ToolProgressDto(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs);

/// <summary>Server-initiated <c>request/permission</c> params.</summary>
public sealed record PermissionDto(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("inputPreview")] string InputPreview);

/// <summary>Server-initiated <c>request/question</c> params.</summary>
public sealed record QuestionDto(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("options")] IReadOnlyList<string> Options,
    [property: JsonPropertyName("multiSelect")] bool MultiSelect);

/// <summary>Server-initiated <c>request/planApproval</c> params.</summary>
public sealed record PlanDto(
    [property: JsonPropertyName("plan")] string Plan);

/// <summary>A single transcript message in a <c>session/history</c> / <c>session/messages</c> result.</summary>
public sealed record HistoryMessageDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>Result of the <c>session/history</c> request.</summary>
public sealed record HistoryResultDto(
    [property: JsonPropertyName("messages")] IReadOnlyList<HistoryMessageDto> Messages);

/// <summary>Result of the <c>session/messages</c> request (incremental, with the next cursor).</summary>
public sealed record MessagesResultDto(
    [property: JsonPropertyName("messages")] IReadOnlyList<HistoryMessageDto> Messages,
    [property: JsonPropertyName("nextIndex")] int NextIndex);

// ---------------------------------------------------------------------------
// Connection
// ---------------------------------------------------------------------------

/// <summary>
/// A thin StreamJsonRpc wrapper that speaks LSP Content-Length-framed JSON-RPC 2.0
/// — the same protocol as <c>coda serve</c>.
/// </summary>
/// <remarks>
/// Wire conventions (match coda exactly):
/// <list type="bullet">
///   <item>Client→server requests: <c>initialize</c>, <c>session/prompt</c>, <c>session/interrupt</c>, <c>session/history</c>, <c>shutdown</c>.</item>
///   <item>Server→client notifications: <c>event/assistantText</c>, <c>event/toolCall</c>, <c>event/toolResult</c>, <c>event/usage</c>, <c>event/streamProgress</c>, <c>event/turnComplete</c>, <c>event/error</c>.</item>
///   <item>Server→client requests: <c>request/permission</c>, <c>request/question</c>, <c>request/planApproval</c>.</item>
/// </list>
/// </remarks>
public sealed class CodaJsonRpcConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly JsonRpc rpc;

    /// <summary>Raised when <c>event/turnComplete</c> is received from coda.</summary>
    public event Action<TurnCompleteDto>? TurnComplete;

    /// <summary>Raised when <c>event/error</c> is received from coda.</summary>
    public event Action<ErrorDto>? ErrorEvent;

    /// <summary>
    /// Raised when <c>event/limitReached</c> is received from coda — a recoverable soft stop
    /// (max_tokens / iteration cap), NOT a crash.
    /// </summary>
    public event Action<LimitReachedDto>? LimitReached;

    /// <summary>Raised when <c>event/toolCall</c> is received from coda.</summary>
    public event Action<ToolCallDto>? ToolCall;

    /// <summary>Raised when <c>event/toolResult</c> is received from coda.</summary>
    public event Action<ToolResultDto>? ToolResult;

    /// <summary>Raised when <c>event/assistantText</c> is received from coda.</summary>
    public event Action<AssistantTextDto>? AssistantText;

    /// <summary>Raised when <c>event/usage</c> is received from coda.</summary>
    public event Action<UsageDto>? Usage;

    /// <summary>Raised when <c>event/streamProgress</c> is received from coda (LLM stream liveness pulse).</summary>
    public event Action<StreamProgressDto>? StreamProgress;

    /// <summary>Raised when <c>event/toolProgress</c> is received from coda (tool-execution liveness pulse).</summary>
    public event Action<ToolProgressDto>? ToolProgress;

    /// <summary>
    /// Called when coda sends <c>request/permission</c>. Return <c>true</c> to allow, <c>false</c> to deny.
    /// If null, denies all permissions.
    /// </summary>
    public Func<PermissionDto, Task<bool>>? OnPermission { get; set; }

    /// <summary>
    /// Called when coda sends <c>request/question</c>. Return the chosen answer string.
    /// If null, returns an empty string.
    /// </summary>
    public Func<QuestionDto, Task<string>>? OnQuestion { get; set; }

    /// <summary>
    /// Called when coda sends <c>request/planApproval</c>. Return <c>true</c> to approve.
    /// If null, rejects all plans.
    /// </summary>
    public Func<PlanDto, Task<bool>>? OnPlanApproval { get; set; }

    /// <summary>
    /// Creates a connection over a bidirectional stream (sending == receiving, e.g. a duplex stream).
    /// </summary>
    public CodaJsonRpcConnection(Stream sendingAndReceiving)
        : this(sendingAndReceiving, sendingAndReceiving)
    {
    }

    /// <summary>
    /// Creates a connection over separate sending and receiving streams.
    /// NOTE: HeaderDelimitedMessageHandler ctor is (sending, receiving).
    /// </summary>
    public CodaJsonRpcConnection(Stream sending, Stream receiving)
    {
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = serializerOptions,
        };

        var handler = new HeaderDelimitedMessageHandler(sending, receiving, formatter);
        this.rpc = new JsonRpc(handler);
        this.RegisterHandlers();
    }

    /// <summary>Starts listening for incoming messages. Must be called before any request.</summary>
    public void Start()
    {
        this.rpc.StartListening();
    }

    /// <summary>
    /// Sends the <c>initialize</c> request and returns the session id the server assigned
    /// together with the optional per-run telemetry log path.
    /// </summary>
    /// <param name="sessionId">
    /// The id to resume. When null or empty, <c>sessionId</c> is omitted from the request so the
    /// server starts a brand-new session (sending an unknown id makes coda fail with -32002).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<InitializeOutcome> InitializeAsync(string? sessionId, CancellationToken ct)
    {
        object parameters = sessionId is { Length: > 0 }
            ? new { protocolVersion = "1", sessionId }
            : new { protocolVersion = "1" };

        var result = await this.rpc
            .InvokeWithParameterObjectAsync<InitializeResultDto>("initialize", parameters, ct)
            .ConfigureAwait(false);

        return new InitializeOutcome(result.SessionId, result.TelemetryLogPath);
    }

    /// <summary>
    /// Sends the <c>session/prompt</c> request with <paramref name="text"/> and waits for a result.
    /// </summary>
    public Task<PromptResultDto> PromptAsync(string text, CancellationToken ct)
    {
        return this.rpc.InvokeWithParameterObjectAsync<PromptResultDto>(
            "session/prompt",
            new { text },
            ct);
    }

    /// <summary>Sends the <c>session/interrupt</c> request.</summary>
    public Task InterruptAsync(CancellationToken ct)
    {
        return this.rpc.InvokeWithParameterObjectAsync<object?>(
            "session/interrupt",
            new { },
            ct);
    }

    /// <summary>
    /// Sends the <c>session/steer</c> request with a steering comment for the running turn and returns
    /// whether coda accepted it (true only when a turn was actually in flight to consume the comment).
    /// Unlike <see cref="PromptAsync"/> this does NOT start a turn; coda injects an accepted comment into
    /// the live turn's next model call.
    /// </summary>
    public async Task<bool> SteerAsync(string text, CancellationToken ct)
    {
        var result = await this.rpc
            .InvokeWithParameterObjectAsync<SteerResultDto>("session/steer", new { text }, ct)
            .ConfigureAwait(false);
        return result?.Ok ?? false;
    }

    /// <summary>Sends the <c>session/history</c> request and returns the full transcript.</summary>
    public Task<HistoryResultDto> HistoryAsync(CancellationToken ct)
    {
        return this.rpc.InvokeWithParameterObjectAsync<HistoryResultDto>(
            "session/history",
            new { },
            ct);
    }

    /// <summary>
    /// Sends the <c>session/messages</c> request for messages after <paramref name="sinceIndex"/>
    /// and returns that slice together with the next cursor.
    /// </summary>
    public Task<MessagesResultDto> MessagesAsync(int sinceIndex, CancellationToken ct)
    {
        return this.rpc.InvokeWithParameterObjectAsync<MessagesResultDto>(
            "session/messages",
            new { sinceIndex },
            ct);
    }

    /// <summary>
    /// Sends the <c>session/setGoal</c> request to set/update/clear the session's autonomous goal
    /// and budget. A null/empty <paramref name="goal"/> clears it. Returns the goal config after
    /// the mutation. The new goal takes effect from the next <c>session/prompt</c>.
    /// </summary>
    public Task<SetGoalResultDto> SetGoalAsync(string? goal, string? maxDuration, int? maxContinuations, CancellationToken ct)
    {
        return this.rpc.InvokeWithParameterObjectAsync<SetGoalResultDto>(
            "session/setGoal",
            new { goal, maxDuration, maxContinuations },
            ct);
    }

    /// <summary>Sends the <c>shutdown</c> request.</summary>
    public Task ShutdownAsync(CancellationToken ct)
    {
        return this.rpc.InvokeWithParameterObjectAsync<object?>(
            "shutdown",
            new { },
            ct);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        this.rpc.Dispose();
        return ValueTask.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private void RegisterHandlers()
    {
        // Notifications: coda → Bridge (fire-and-forget, no reply needed).
        // IMPORTANT: StreamJsonRpc dispatches named-param messages to handlers with matching
        // individual parameter names. Using Func<JsonNode?,...> does NOT work for named params —
        // the handler must declare each field as a separate parameter (same names as the JSON keys).
        this.rpc.AddLocalRpcMethod(
            "event/turnComplete",
            new Func<string?, bool, Task>((stopReason, interrupted) =>
            {
                this.TurnComplete?.Invoke(new TurnCompleteDto(stopReason, interrupted));
                return Task.CompletedTask;
            }));

        this.rpc.AddLocalRpcMethod(
            "event/error",
            new Func<string, Task>(message =>
            {
                this.ErrorEvent?.Invoke(new ErrorDto(message));
                return Task.CompletedTask;
            }));

        this.rpc.AddLocalRpcMethod(
            "event/limitReached",
            new Func<string, string, Task>((kind, message) =>
            {
                this.LimitReached?.Invoke(new LimitReachedDto(kind, message));
                return Task.CompletedTask;
            }));

        this.rpc.AddLocalRpcMethod(
            "event/toolCall",
            new Func<string, string, Task>((toolName, inputJson) =>
            {
                this.ToolCall?.Invoke(new ToolCallDto(toolName, inputJson));
                return Task.CompletedTask;
            }));

        this.rpc.AddLocalRpcMethod(
            "event/toolResult",
            new Func<string, string?, Task>((toolName, outputJson) =>
            {
                this.ToolResult?.Invoke(new ToolResultDto(toolName, outputJson));
                return Task.CompletedTask;
            }));

        this.rpc.AddLocalRpcMethod(
            "event/assistantText",
            new Func<string, Task>(delta =>
            {
                this.AssistantText?.Invoke(new AssistantTextDto(delta));
                return Task.CompletedTask;
            }));

        this.rpc.AddLocalRpcMethod(
            "event/usage",
            new Func<long, long, Task>((inputTokens, outputTokens) =>
            {
                this.Usage?.Invoke(new UsageDto(inputTokens, outputTokens));
                return Task.CompletedTask;
            }));

        this.rpc.AddLocalRpcMethod(
            "event/streamProgress",
            new Func<string, int, int, long, Task>((phase, chunks, chars, elapsedMs) =>
            {
                this.StreamProgress?.Invoke(new StreamProgressDto(phase, chunks, chars, elapsedMs));
                return Task.CompletedTask;
            }));

        this.rpc.AddLocalRpcMethod(
            "event/toolProgress",
            new Func<string, long, Task>((toolName, elapsedMs) =>
            {
                this.ToolProgress?.Invoke(new ToolProgressDto(toolName, elapsedMs));
                return Task.CompletedTask;
            }));

        // Server-initiated requests: coda → Bridge, Bridge replies.
        // Named params from InvokeWithParameterObjectAsync must match individual parameter names.
        this.rpc.AddLocalRpcMethod(
            "request/permission",
            new Func<string, string, CancellationToken, Task<JsonNode?>>(async (toolName, inputPreview, ct) =>
            {
                var dto = new PermissionDto(toolName, inputPreview);
                var allow = false;
                if (this.OnPermission is not null)
                {
                    allow = await this.OnPermission(dto).ConfigureAwait(false);
                }

                return new JsonObject { ["allow"] = allow };
            }));

        this.rpc.AddLocalRpcMethod(
            "request/question",
            new Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<JsonNode?>>(
                async (question, options, multiSelect, ct) =>
                {
                    var dto = new QuestionDto(question, options, multiSelect);
                    var answer = string.Empty;
                    if (this.OnQuestion is not null)
                    {
                        answer = await this.OnQuestion(dto).ConfigureAwait(false);
                    }

                    return new JsonObject { ["answer"] = answer };
                }));

        this.rpc.AddLocalRpcMethod(
            "request/planApproval",
            new Func<string, CancellationToken, Task<JsonNode?>>(async (plan, ct) =>
            {
                var dto = new PlanDto(plan);
                var approve = false;
                if (this.OnPlanApproval is not null)
                {
                    approve = await this.OnPlanApproval(dto).ConfigureAwait(false);
                }

                return new JsonObject { ["approve"] = approve };
            }));
    }

    private static T? Deserialize<T>(JsonNode? node)
    {
        if (node is null)
        {
            return default;
        }

        return node.Deserialize<T>(serializerOptions);
    }
}
