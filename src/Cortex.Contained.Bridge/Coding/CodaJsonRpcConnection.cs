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
    [property: JsonPropertyName("interrupted")] bool Interrupted)
{
    /// <inheritdoc cref="ToolCallDto.RootTurnId" />
    [JsonPropertyName("rootTurnId")]
    public string? RootTurnId { get; init; }

    /// <inheritdoc cref="ToolCallDto.ActivityId" />
    [JsonPropertyName("activityId")]
    public string? ActivityId { get; init; }
}

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
    [property: JsonPropertyName("inputJson")] string InputJson)
{
    /// <summary>Identifies the root turn this call belongs to. Absent on older coda builds.</summary>
    [JsonPropertyName("rootTurnId")]
    public string? RootTurnId { get; init; }

    /// <summary>Identifies the activity (main agent or a subagent) that issued the call.</summary>
    [JsonPropertyName("activityId")]
    public string? ActivityId { get; init; }

    /// <summary>Correlates this call with its <c>event/toolProgress</c> and <c>event/toolResult</c>.</summary>
    [JsonPropertyName("callId")]
    public string? CallId { get; init; }

    /// <summary>Identifies the tool source (built-in, MCP server, plugin).</summary>
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }
}

/// <summary>
/// Payload of the <c>event/toolResult</c> notification. coda names the result body
/// <c>content</c> (not <c>outputJson</c>) and pairs it with an <c>isError</c> flag.
/// </summary>
public sealed record ToolResultDto(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("isError")] bool IsError = false)
{
    /// <inheritdoc cref="ToolCallDto.RootTurnId" />
    [JsonPropertyName("rootTurnId")]
    public string? RootTurnId { get; init; }

    /// <inheritdoc cref="ToolCallDto.ActivityId" />
    [JsonPropertyName("activityId")]
    public string? ActivityId { get; init; }

    /// <inheritdoc cref="ToolCallDto.CallId" />
    [JsonPropertyName("callId")]
    public string? CallId { get; init; }

    /// <inheritdoc cref="ToolCallDto.SourceId" />
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }

    /// <summary>Terminal status coda assigned the call (e.g. <c>Completed</c>, <c>Failed</c>).</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

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
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs)
{
    /// <inheritdoc cref="ToolCallDto.RootTurnId" />
    [JsonPropertyName("rootTurnId")]
    public string? RootTurnId { get; init; }

    /// <inheritdoc cref="ToolCallDto.ActivityId" />
    [JsonPropertyName("activityId")]
    public string? ActivityId { get; init; }

    /// <inheritdoc cref="ToolCallDto.CallId" />
    [JsonPropertyName("callId")]
    public string? CallId { get; init; }

    /// <inheritdoc cref="ToolCallDto.SourceId" />
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }
}

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
///   <item>Server→client notifications: <c>event/assistantText</c>, <c>event/toolCall</c>, <c>event/toolResult</c>, <c>event/toolProgress</c>, <c>event/usage</c>, <c>event/streamProgress</c>, <c>event/turnComplete</c>, <c>event/limitReached</c>, <c>event/error</c>.</item>
///   <item>Server→client requests: <c>request/permission</c>, <c>request/question</c>, <c>request/planApproval</c>.</item>
/// </list>
/// <para>
/// Inbound payloads are bound whole-object rather than parameter-by-parameter so coda can add
/// fields without breaking dispatch — see <c>RegisterHandlers</c>. coda omits null fields
/// (<c>WhenWritingNull</c>), so every optional DTO member must stay nullable/defaulted.
/// </para>
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
        // Every inbound coda→Bridge message is bound with UseSingleObjectParameterDeserialization
        // (see CodaInboundTarget) — the whole params object is deserialized into one DTO. This is
        // deliberate and load-bearing, NOT a style choice.
        //
        // StreamJsonRpc's default named-parameter binding requires the params object to match the
        // handler's parameter list EXACTLY: a member coda ADDS makes dispatch fail, and so does an
        // optional one coda OMITS (it serializes with WhenWritingNull, so null fields vanish from
        // the wire). For a notification that failure is completely SILENT — no error, no log.
        //
        // That is precisely how the tool-correlation fields coda added to event/toolCall,
        // event/toolProgress and event/toolResult (rootTurnId/activityId/callId/sourceId) silently
        // switched off the tool-execution liveness pulse: the Bridge stopped seeing any activity
        // while a tool ran, so the idle watchdog reaped healthy sessions mid-tool-call and the
        // resulting process kill surfaced as "the JSON-RPC connection was lost".
        //
        // Single-object deserialization ignores unknown members and leaves absent optional ones at
        // their defaults, so coda can extend any payload without breaking the Bridge.
        this.rpc.AddLocalRpcTarget(new CodaInboundTarget(this), null);
    }

    private void RaiseTurnComplete(TurnCompleteDto dto) => this.TurnComplete?.Invoke(dto);

    private void RaiseErrorEvent(ErrorDto dto) => this.ErrorEvent?.Invoke(dto);

    private void RaiseLimitReached(LimitReachedDto dto) => this.LimitReached?.Invoke(dto);

    private void RaiseToolCall(ToolCallDto dto) => this.ToolCall?.Invoke(dto);

    private void RaiseToolResult(ToolResultDto dto) => this.ToolResult?.Invoke(dto);

    private void RaiseAssistantText(AssistantTextDto dto) => this.AssistantText?.Invoke(dto);

    private void RaiseUsage(UsageDto dto) => this.Usage?.Invoke(dto);

    private void RaiseStreamProgress(StreamProgressDto dto) => this.StreamProgress?.Invoke(dto);

    private void RaiseToolProgress(ToolProgressDto dto) => this.ToolProgress?.Invoke(dto);

    /// <summary>
    /// The RPC target holding every coda→Bridge handler: notifications (fire-and-forget) and
    /// server-initiated requests (Bridge replies). Each method takes a single DTO bound from the
    /// whole params object — see <see cref="RegisterHandlers"/> for why that matters.
    /// </summary>
    /// <remarks>
    /// Only attributed handler methods may be public here: <c>AddLocalRpcTarget</c> exposes every
    /// public method on the target, so anything else added would become a callable RPC method.
    /// </remarks>
    private sealed class CodaInboundTarget
    {
        private readonly CodaJsonRpcConnection connection;

        public CodaInboundTarget(CodaJsonRpcConnection connection)
        {
            this.connection = connection;
        }

        [JsonRpcMethod("event/turnComplete", UseSingleObjectParameterDeserialization = true)]
        public void OnTurnComplete(TurnCompleteDto dto) => this.connection.RaiseTurnComplete(dto);

        [JsonRpcMethod("event/error", UseSingleObjectParameterDeserialization = true)]
        public void OnError(ErrorDto dto) => this.connection.RaiseErrorEvent(dto);

        [JsonRpcMethod("event/limitReached", UseSingleObjectParameterDeserialization = true)]
        public void OnLimitReached(LimitReachedDto dto) => this.connection.RaiseLimitReached(dto);

        [JsonRpcMethod("event/toolCall", UseSingleObjectParameterDeserialization = true)]
        public void OnToolCall(ToolCallDto dto) => this.connection.RaiseToolCall(dto);

        [JsonRpcMethod("event/toolResult", UseSingleObjectParameterDeserialization = true)]
        public void OnToolResult(ToolResultDto dto) => this.connection.RaiseToolResult(dto);

        [JsonRpcMethod("event/assistantText", UseSingleObjectParameterDeserialization = true)]
        public void OnAssistantText(AssistantTextDto dto) => this.connection.RaiseAssistantText(dto);

        [JsonRpcMethod("event/usage", UseSingleObjectParameterDeserialization = true)]
        public void OnUsage(UsageDto dto) => this.connection.RaiseUsage(dto);

        [JsonRpcMethod("event/streamProgress", UseSingleObjectParameterDeserialization = true)]
        public void OnStreamProgress(StreamProgressDto dto) => this.connection.RaiseStreamProgress(dto);

        [JsonRpcMethod("event/toolProgress", UseSingleObjectParameterDeserialization = true)]
        public void OnToolProgress(ToolProgressDto dto) => this.connection.RaiseToolProgress(dto);

        [JsonRpcMethod("request/permission", UseSingleObjectParameterDeserialization = true)]
        public async Task<JsonNode?> OnPermissionRequestAsync(PermissionDto dto)
        {
            var allow = false;
            if (this.connection.OnPermission is not null)
            {
                allow = await this.connection.OnPermission(dto).ConfigureAwait(false);
            }

            return new JsonObject { ["allow"] = allow };
        }

        [JsonRpcMethod("request/question", UseSingleObjectParameterDeserialization = true)]
        public async Task<JsonNode?> OnQuestionRequestAsync(QuestionDto dto)
        {
            var answer = string.Empty;
            if (this.connection.OnQuestion is not null)
            {
                answer = await this.connection.OnQuestion(dto).ConfigureAwait(false);
            }

            return new JsonObject { ["answer"] = answer };
        }

        [JsonRpcMethod("request/planApproval", UseSingleObjectParameterDeserialization = true)]
        public async Task<JsonNode?> OnPlanApprovalRequestAsync(PlanDto dto)
        {
            var approve = false;
            if (this.connection.OnPlanApproval is not null)
            {
                approve = await this.connection.OnPlanApproval(dto).ConfigureAwait(false);
            }

            return new JsonObject { ["approve"] = approve };
        }
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
