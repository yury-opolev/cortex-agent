using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// One running <c>coda serve</c> subprocess plus its JSON-RPC connection and in-memory state.
/// Public methods are thread-safe; only one user message can be in-flight at a time
/// (enforced by the <see cref="State"/> machine).
/// </summary>
/// <remarks>
/// The <c>internal</c> constructor (accepting a pre-built <see cref="CodaJsonRpcConnection"/>)
/// is the TEST SEAM: integration tests inject an in-memory connection without spawning a real
/// process.  The public constructor (production path) defers connection creation to
/// <see cref="StartAsync"/> where the subprocess's stdio streams are available.
/// </remarks>
public sealed partial class CodaSession : IAsyncDisposable
{
    private const int MaxRecentToolCalls = 5;

    private readonly ILogger<CodaSession> logger;
    private readonly CodaOptions options;
    private readonly bool ownsProcess;
    private readonly Lock stateLock = new();
    private readonly StringBuilder stderrBuffer = new();
    private readonly ConcurrentQueue<CodingToolCall> currentTaskToolCalls = new();
    private readonly ConcurrentQueue<CodingToolCall> recentToolCallHistory = new();

    // Parked server-request TaskCompletionSources, keyed by requestId.
    private readonly ConcurrentDictionary<string, PendingRequest> pendingRequests = new();

    // Null only during production construction, before StartAsync creates it from stdio.
    private CodaJsonRpcConnection? connection;

    private Process? process;
    private Task? stderrLoopTask;
    private PeriodicTimer? watchdogTimer;
    private Task? watchdogTask;
    private string? currentTaskId;
    private string assistantBuffer = string.Empty;
    private string? currentActivity;
    private CodingGoalStatus? goalStatus;

    // Live LLM-stream snapshot, fed by event/streamProgress. Cross-thread → Interlocked.
    private long isStreaming;       // 0/1
    private long streamChunks = -1; // -1 == no pulse yet
    private long streamChars = -1;  // -1 == no pulse yet
    private long lastStreamTicks;   // 0 == never

    private static CancellationTokenSource LinkedTimeout(int seconds, CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, seconds)));
        return cts;
    }

    // -----------------------------------------------------------------------
    // Production constructor: connection is created from spawned-process stdio in StartAsync.
    // -----------------------------------------------------------------------

    public CodaSession(
        string sessionId,
        string channelId,
        string workingFolder,
        CodingPolicy policy,
        CodaOptions options,
        ILogger<CodaSession> logger,
        string? goal = null,
        bool sessionMemory = false)
    {
        this.SessionId = sessionId;
        this.ChannelId = channelId;
        this.WorkingFolder = workingFolder;
        this.Policy = policy;
        this.options = options;
        this.logger = logger;
        this.ownsProcess = true;
        this.CreatedAt = DateTimeOffset.UtcNow;
        this.LastActivityAt = this.CreatedAt;
        this.Goal = goal;
        this.SessionMemory = sessionMemory;
    }

    // -----------------------------------------------------------------------
    // Test seam: accepts a pre-built connection — no process is ever spawned.
    // -----------------------------------------------------------------------

    internal CodaSession(
        string sessionId,
        string channelId,
        string workingFolder,
        CodingPolicy policy,
        CodaJsonRpcConnection connection,
        ILogger<CodaSession> logger,
        CodaOptions? options = null)
    {
        this.SessionId = sessionId;
        this.ChannelId = channelId;
        this.WorkingFolder = workingFolder;
        this.Policy = policy;
        this.options = options ?? new CodaOptions();
        this.connection = connection;
        this.logger = logger;
        this.ownsProcess = false;
        this.CreatedAt = DateTimeOffset.UtcNow;
        this.LastActivityAt = this.CreatedAt;
        this.WireConnectionEvents();
    }

    // -----------------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------------

    public event Action<CodaFinalResultEvent>? FinalResult;
    public event Action<CodaErrorEvent>? Error;
    public event Action<CodaStalledEvent>? Stalled;

    /// <summary>Raised when coda ended a turn early on a recoverable limit (max_tokens / iteration cap) — not a crash.</summary>
    public event Action<CodaLimitReachedEvent>? LimitReached;
    public event Action<CodaPermissionRequestEvent>? PermissionRequested;
    public event Action<CodaQuestionEvent>? Question;
    public event Action<CodaPlanApprovalEvent>? PlanApproval;

    // -----------------------------------------------------------------------
    // Public snapshot surface (read by the manager)
    // -----------------------------------------------------------------------

    public string SessionId { get; }

    /// <summary>
    /// The owning tenant id. Set by <see cref="CodaSessionManager"/> after construction;
    /// used only for the per-tenant concurrency ceiling.
    /// </summary>
    public string? TenantId { get; set; }

    public string ChannelId { get; }

    public string WorkingFolder { get; }

    public CodingPolicy Policy { get; }

    public string? Goal { get; }

    public bool SessionMemory { get; }

    public DateTimeOffset CreatedAt { get; }

    private long lastActivityTicks;

    /// <summary>
    /// Last time coda showed activity (UTC). Read/written from multiple threads
    /// (RPC callbacks, message submission, and the idle watchdog), so access goes
    /// through <see cref="Interlocked"/> to avoid torn/stale reads of the struct.
    /// </summary>
    public DateTimeOffset LastActivityAt
    {
        get => new DateTimeOffset(Interlocked.Read(ref this.lastActivityTicks), TimeSpan.Zero);
        private set => Interlocked.Exchange(ref this.lastActivityTicks, value.UtcDateTime.Ticks);
    }

    /// <summary>True while coda is actively streaming an LLM response (between first-token and complete).</summary>
    public bool IsStreaming => Interlocked.Read(ref this.isStreaming) == 1;

    /// <summary>Latest streamed-chunk count for the in-flight LLM call, or null before any pulse.</summary>
    public long? StreamedChunks
    {
        get
        {
            var value = Interlocked.Read(ref this.streamChunks);
            return value < 0 ? null : value;
        }
    }

    /// <summary>Latest streamed-char count for the in-flight LLM call, or null before any pulse.</summary>
    public long? StreamedChars
    {
        get
        {
            var value = Interlocked.Read(ref this.streamChars);
            return value < 0 ? null : value;
        }
    }

    /// <summary>UTC time of the last <c>event/streamProgress</c> pulse, or null if none this session.</summary>
    public DateTimeOffset? LastStreamActivityAt
    {
        get
        {
            var ticks = Interlocked.Read(ref this.lastStreamTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Human-readable "what coda is doing right now", derived from the latest pulse (null if unknown).</summary>
    public string? CurrentActivity
    {
        get
        {
            lock (this.stateLock)
            {
                return this.currentActivity;
            }
        }
    }

    /// <summary>Status of the most recent autonomous-goal run, or null if none has completed.</summary>
    public CodingGoalStatus? GoalStatus
    {
        get
        {
            lock (this.stateLock)
            {
                return this.goalStatus;
            }
        }
    }

    public CodingSessionState State { get; private set; } = CodingSessionState.Idle;

    public string? LastUserMessage { get; private set; }

    public string? LastAssistantSummary { get; private set; }

    /// <summary>Per-run telemetry log path coda reported at <c>initialize</c> (null if none).</summary>
    public string? TelemetryLogPath { get; private set; }

    /// <summary>The most recent error message raised for this session (null if none).</summary>
    public string? LastError { get; private set; }

    private long inputTokens = -1;
    private long outputTokens = -1;

    /// <summary>
    /// Latest cumulative input-token total streamed by coda via <c>event/usage</c>,
    /// or null before any usage event arrives. Read from multiple threads.
    /// </summary>
    public long? InputTokens
    {
        get
        {
            var value = Interlocked.Read(ref this.inputTokens);
            return value < 0 ? null : value;
        }
    }

    /// <summary>
    /// Latest cumulative output-token total streamed by coda via <c>event/usage</c>,
    /// or null before any usage event arrives. Read from multiple threads.
    /// </summary>
    public long? OutputTokens
    {
        get
        {
            var value = Interlocked.Read(ref this.outputTokens);
            return value < 0 ? null : value;
        }
    }

    public string? CurrentTaskId
    {
        get
        {
            lock (this.stateLock)
            {
                return this.currentTaskId;
            }
        }
    }

    public IReadOnlyList<CodingToolCall> RecentToolCalls => [.. this.recentToolCallHistory];

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <summary>
    /// Starts the session.  If the production constructor was used, spawns <c>coda serve</c>
    /// and builds the connection from its stdio.  If the test-seam constructor was used, the
    /// connection is already wired and this just sends <c>initialize</c>.
    /// </summary>
    public async Task StartAsync(bool isResume, CancellationToken cancellationToken)
    {
        if (this.ownsProcess)
        {
            this.connection = await this.SpawnAndConnectAsync(isResume, cancellationToken)
                .ConfigureAwait(false);
            this.WireConnectionEvents();
        }

        this.connection!.Start();

        using var initCts = LinkedTimeout(this.options.StartTimeoutSeconds, cancellationToken);
        var resumeId = isResume ? this.SessionId : null;
        var outcome = await this.connection
            .InitializeAsync(resumeId, initCts.Token)
            .ConfigureAwait(false);

        this.TelemetryLogPath = outcome.TelemetryLogPath;

        this.LogSessionStarted(this.SessionId, outcome.SessionId, this.WorkingFolder, this.Policy);

        this.StartWatchdog();
    }

    // -----------------------------------------------------------------------
    // Idle watchdog: terminates a session that goes silent mid-turn.
    // -----------------------------------------------------------------------

    private void StartWatchdog()
    {
        var idleSeconds = Math.Max(1, this.options.PromptIdleTimeoutSeconds);
        var tickSeconds = Math.Min(idleSeconds / 4.0, 30);
        var tick = TimeSpan.FromSeconds(tickSeconds);
        if (tick < TimeSpan.FromMilliseconds(50))
        {
            tick = TimeSpan.FromMilliseconds(50);
        }

        this.watchdogTimer = new PeriodicTimer(tick);
        this.watchdogTask = Task.Run(() => this.WatchdogLoopAsync(idleSeconds));
    }

    private async Task WatchdogLoopAsync(int idleSeconds)
    {
        var timer = this.watchdogTimer!;
        try
        {
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                bool working;
                lock (this.stateLock)
                {
                    working = this.State == CodingSessionState.Working;
                }

                if (!working)
                {
                    continue;
                }

                var idleFor = DateTimeOffset.UtcNow - this.LastActivityAt;
                if (idleFor < TimeSpan.FromSeconds(idleSeconds))
                {
                    continue;
                }

                // Re-confirm we're still mid-turn before declaring frozen — the turn may
                // have just completed in the gap since the check above.
                lock (this.stateLock)
                {
                    if (this.State != CodingSessionState.Working)
                    {
                        continue;
                    }
                }

                var stderrTail = this.GetStderrTail();
                var idleForSeconds = (int)idleFor.TotalSeconds;
                this.LogWatchdogFrozen(this.SessionId, idleForSeconds);

                var message = $"coda appears stalled — no activity for {idleForSeconds}s; terminating session.";

                // Stall-specific signal (carries liveness context) so the orchestrator can relay
                // status=stalled and resume rather than churn. The generic Error path is also kept
                // so existing terminal handling (state=Crashed, metadata) is unchanged.
                this.Stalled?.Invoke(new CodaStalledEvent(
                    this.SessionId,
                    idleForSeconds,
                    this.IsStreaming,
                    this.StreamedChars,
                    stderrTail,
                    message));

                // Mark crashed WITHOUT firing the generic Error event — the stall is relayed once,
                // via Stalled, so the agent never receives a duplicate crashed+stalled injection.
                this.MarkCrashed(message);

                try
                {
                    this.process?.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited (or test seam with no process).
                }

                return;
            }
        }
        catch (OperationCanceledException)
        {
            // expected on dispose
        }
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for spawning <c>coda serve</c>.
    /// <para>
    /// CRITICAL: do NOT set <c>StandardInputEncoding</c> / <c>StandardOutputEncoding</c>.
    /// We drive stdin/stdout as RAW byte streams via <c>proc.Standard*.BaseStream</c>
    /// (StreamJsonRpc's <c>HeaderDelimitedMessageHandler</c>). Setting an output encoding makes
    /// .NET attach a <see cref="StreamReader"/> over the child's stdout that eagerly buffers its
    /// bytes — so the raw <c>BaseStream</c> read sees EOF (0 bytes) and the JSON-RPC
    /// <c>initialize</c> handshake never completes (coda "hangs", start times out). stderr IS
    /// read via the <see cref="StreamReader"/> (<c>ReadStderrLoopAsync</c>), so its encoding is
    /// set so coda's UTF-8 diagnostics decode correctly.
    /// </para>
    /// </summary>
    internal static ProcessStartInfo BuildProcessStartInfo(
        string codaBinaryPath,
        string workingFolder,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = codaBinaryPath,
            WorkingDirectory = workingFolder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // Overlay any policy-derived variables (e.g. CODA_USER_MCP_DIR for curated MCP) onto the
        // inherited environment, so the spawned coda picks them up without touching the Bridge's own.
        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
            {
                psi.Environment[key] = value;
            }
        }

        return psi;
    }

    private Task<CodaJsonRpcConnection> SpawnAndConnectAsync(bool isResume, CancellationToken cancellationToken)
    {
        if (this.process is not null)
        {
            throw new InvalidOperationException("Session already started.");
        }

        var args = CodaServeArgsBuilder.Build(
            this.SessionId,
            this.WorkingFolder,
            this.Policy,
            isResume,
            this.options.Provider,
            this.Goal,
            this.SessionMemory,
            this.options.Mcp);

        // Curated MCP policy exports CODA_USER_MCP_DIR so the spawned coda reads the curated
        // .mcp.json instead of the host user's personal one; Host/Off add nothing here.
        var extraEnv = CodaMcpEnvironment.Resolve(this.options.Mcp, this.options.CuratedMcpDir);
        var psi = BuildProcessStartInfo(this.options.CodaBinaryPath, this.WorkingFolder, args, extraEnv);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Exited += this.OnProcessExited;

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to launch '{this.options.CodaBinaryPath}'.");
        }

        lock (this.stateLock)
        {
            this.process = proc;
            this.State = CodingSessionState.Idle;
        }

        this.stderrLoopTask = Task.Run(
            () => this.ReadStderrLoopAsync(proc, cancellationToken),
            cancellationToken);

        // LSP framing: sending = stdin, receiving = stdout.
        var conn = new CodaJsonRpcConnection(proc.StandardInput.BaseStream, proc.StandardOutput.BaseStream);
        return Task.FromResult(conn);
    }

    /// <summary>
    /// Sends a user message to coda and returns the new <c>taskId</c>.
    /// The prompt runs in the background; <see cref="FinalResult"/> is raised when done.
    /// </summary>
    public Task<string> WriteUserMessageAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        string taskId;
        lock (this.stateLock)
        {
            if (this.State is CodingSessionState.Working
                or CodingSessionState.AwaitingPermission
                or CodingSessionState.AwaitingQuestion
                or CodingSessionState.AwaitingPlan)
            {
                throw new InvalidOperationException("Session is busy with another task.");
            }

            if (this.State is CodingSessionState.Crashed or CodingSessionState.Ended)
            {
                throw new InvalidOperationException("Session is not running.");
            }

            taskId = Guid.NewGuid().ToString("N");
            this.currentTaskId = taskId;
            this.State = CodingSessionState.Working;
            this.LastActivityAt = DateTimeOffset.UtcNow;
            this.LastUserMessage = Truncate(text, 500);
            this.assistantBuffer = string.Empty;
            this.currentActivity = null;
            this.currentTaskToolCalls.Clear();
        }

        Interlocked.Exchange(ref this.isStreaming, 0);

        // Fire-and-forget background prompt.  TurnComplete notification drives FinalResult.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await this.connection!.PromptAsync(text, cancellationToken).ConfigureAwait(false);
                // For an autonomous-goal session the whole goal run completes here; capture the
                // resulting goalStatus so coding_session_status can report the run outcome.
                this.ApplyGoalStatus(result.GoalStatus);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.RaiseError(null, null, $"coda prompt failed: {ex.Message}");
            }
        }, cancellationToken);

        return Task.FromResult(taskId);
    }

    /// <summary>
    /// Attempts to steer the running turn. Captures (State, CurrentTaskId) atomically under the state
    /// lock: steers only when the session is <c>Working</c>, and reports success only when coda confirms a
    /// turn was actually in flight to consume the comment. Returns <c>(false, null)</c> otherwise so the
    /// caller can deliver the message as a normal new turn — a mid-turn message is never silently lost.
    /// </summary>
    public async Task<(bool Steered, string? TaskId)> TrySteerAsync(string comment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);

        string? taskId;
        lock (this.stateLock)
        {
            if (this.State != CodingSessionState.Working)
            {
                return (false, null);
            }

            taskId = this.currentTaskId;
        }

        this.LastActivityAt = DateTimeOffset.UtcNow;
        var accepted = await this.connection!.SteerAsync(comment, cancellationToken).ConfigureAwait(false);
        return accepted ? (true, taskId) : (false, null);
    }

    /// <summary>
    /// Sets, updates, or clears the session's autonomous goal and budget (coda <c>session/setGoal</c>).
    /// A null/empty <paramref name="goal"/> clears it. Returns the goal config after the mutation.
    /// The new goal takes effect from the next message sent to the session.
    /// </summary>
    public async Task<(string? Goal, string? MaxDuration, int? MaxContinuations)> SetGoalAsync(
        string? goal, string? maxDuration, int? maxContinuations, CancellationToken cancellationToken)
    {
        using var ctrlCts = LinkedTimeout(this.options.ControlTimeoutSeconds, cancellationToken);
        var result = await this.connection!
            .SetGoalAsync(goal, maxDuration, maxContinuations, ctrlCts.Token)
            .ConfigureAwait(false);
        return (result.Goal, result.MaxDuration, result.MaxContinuations);
    }

    private void ApplyGoalStatus(GoalStatusDto? dto)
    {
        if (dto is null)
        {
            // A non-goal turn produced no goalStatus — keep the last goal run's status (if any).
            return;
        }

        var mapped = new CodingGoalStatus
        {
            Outcome = dto.Outcome,
            Remaining = dto.Remaining,
            Continuations = dto.Continuations,
            ElapsedSeconds = dto.ElapsedSeconds,
            Escalated = dto.Escalated,
            ExtensionUsed = dto.ExtensionUsed,
        };

        lock (this.stateLock)
        {
            this.goalStatus = mapped;
        }
    }

    /// <summary>
    /// Sends an interrupt signal to the running coda session.
    /// </summary>
    public async Task<string?> InterruptAsync(CancellationToken cancellationToken)
    {
        string? interruptedTaskId;
        lock (this.stateLock)
        {
            interruptedTaskId = this.currentTaskId;
        }

        try
        {
            using var ctrlCts = LinkedTimeout(this.options.ControlTimeoutSeconds, cancellationToken);
            await this.connection!.InterruptAsync(ctrlCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogInterruptError(ex);
        }

        return interruptedTaskId;
    }

    /// <summary>
    /// Fetches the full conversation transcript from coda (<c>session/history</c>).
    /// </summary>
    public async Task<IReadOnlyList<HistoryMessageDto>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        using var ctrlCts = LinkedTimeout(this.options.ControlTimeoutSeconds, cancellationToken);
        var result = await this.connection!.HistoryAsync(ctrlCts.Token).ConfigureAwait(false);
        return result.Messages;
    }

    /// <summary>
    /// Fetches the transcript messages after <paramref name="sinceIndex"/> from coda
    /// (<c>session/messages</c>), together with the next cursor.
    /// </summary>
    public async Task<(IReadOnlyList<HistoryMessageDto> Messages, int NextIndex)> GetMessagesAsync(
        int sinceIndex, CancellationToken cancellationToken)
    {
        using var ctrlCts = LinkedTimeout(this.options.ControlTimeoutSeconds, cancellationToken);
        var result = await this.connection!.MessagesAsync(sinceIndex, ctrlCts.Token).ConfigureAwait(false);
        return (result.Messages, result.NextIndex);
    }

    /// <summary>
    /// Gracefully shuts down the coda session and terminates the subprocess.
    /// </summary>
    public async Task EndAsync(CancellationToken cancellationToken)
    {
        lock (this.stateLock)
        {
            this.State = CodingSessionState.Ended;
        }

        if (this.connection is not null)
        {
            try
            {
                using var ctrlCts = LinkedTimeout(this.options.ControlTimeoutSeconds, cancellationToken);
                await this.connection.ShutdownAsync(ctrlCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogShutdownError(ex);
            }
        }

        if (this.process is not null)
        {
            try
            {
                using var killCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                killCts.CancelAfter(TimeSpan.FromSeconds(5));
                await this.process.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    this.process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.EndAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogDisposeError(ex);
        }

        if (this.stderrLoopTask is not null)
        {
            try
            {
                await this.stderrLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        if (this.connection is not null)
        {
            await this.connection.DisposeAsync().ConfigureAwait(false);
        }

        this.watchdogTimer?.Dispose();
        if (this.watchdogTask is not null)
        {
            try
            {
                await this.watchdogTask.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        this.process?.Dispose();
    }

    // -----------------------------------------------------------------------
    // Respond to parked server-requests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves a parked permission / question / plan TCS by <paramref name="requestId"/>.
    /// <para>
    /// Permission: <c>"allow_once"</c> | <c>"allow_always"</c> → <c>true</c>; <c>"deny"</c> → <c>false</c>.<br/>
    /// Plan: <c>"approve"</c> → <c>true</c>; <c>"reject"</c> → <c>false</c>.<br/>
    /// Question: the raw <paramref name="response"/> string is the answer.
    /// </para>
    /// The allow-always cache lives on the manager (Task 4); this method only resolves the TCS.
    /// </summary>
    public Task RespondAsync(string requestId, string response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (!this.pendingRequests.TryRemove(requestId, out var pending))
        {
            return Task.CompletedTask;
        }

        switch (pending)
        {
            case PendingRequest.Permission perm:
                var allow = response is "allow_once" or "allow_always";
                perm.Tcs.TrySetResult(allow);
                break;

            case PendingRequest.Plan plan:
                var approve = response == "approve";
                plan.Tcs.TrySetResult(approve);
                break;

            case PendingRequest.Question question:
                question.Tcs.TrySetResult(response);
                break;
        }

        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Private: wire connection events
    // -----------------------------------------------------------------------

    private void WireConnectionEvents()
    {
        this.connection!.TurnComplete += this.OnTurnComplete;
        this.connection.ErrorEvent += this.OnErrorNotification;
        this.connection.LimitReached += this.OnLimitReachedNotification;
        this.connection.ToolCall += this.OnToolCall;
        this.connection.AssistantText += this.OnAssistantText;
        this.connection.Usage += this.OnUsage;
        this.connection.StreamProgress += this.OnStreamProgress;

        this.connection.OnPermission = this.HandlePermissionRequestAsync;
        this.connection.OnQuestion = this.HandleQuestionRequestAsync;
        this.connection.OnPlanApproval = this.HandlePlanApprovalRequestAsync;
    }

    private void OnTurnComplete(TurnCompleteDto dto)
    {
        this.LastActivityAt = DateTimeOffset.UtcNow;

        string taskId;
        string finalText;
        lock (this.stateLock)
        {
            taskId = this.currentTaskId ?? Guid.NewGuid().ToString("N");
            this.currentTaskId = null;
            this.State = CodingSessionState.Idle;
            finalText = this.assistantBuffer;
            this.LastAssistantSummary = Truncate(finalText, 500);
            this.currentActivity = null;
        }

        Interlocked.Exchange(ref this.isStreaming, 0);

        var calls = this.currentTaskToolCalls.ToArray();
        this.currentTaskToolCalls.Clear();

        this.FinalResult?.Invoke(new CodaFinalResultEvent(
            this.SessionId,
            taskId,
            finalText,
            calls));
    }

    private void OnErrorNotification(ErrorDto dto)
    {
        this.LastActivityAt = DateTimeOffset.UtcNow;
        this.RaiseError(null, null, dto.Message);
    }

    private void OnLimitReachedNotification(LimitReachedDto dto)
    {
        // A recoverable soft stop (max_tokens / iteration cap) — NOT a crash, so we deliberately do
        // NOT MarkCrashed here. coda ends the turn normally right after this (event/turnComplete →
        // Idle); we only surface the distinct signal so the orchestrator can relay it and offer to
        // continue the run rather than treating it as a failure.
        this.LastActivityAt = DateTimeOffset.UtcNow;
        this.LimitReached?.Invoke(new CodaLimitReachedEvent(this.SessionId, dto.Kind, dto.Message));
    }

    private void OnToolCall(ToolCallDto dto)
    {
        this.LastActivityAt = DateTimeOffset.UtcNow;

        var summary = SummarizeInput(dto.InputJson);
        var call = new CodingToolCall
        {
            Name = dto.ToolName,
            ArgsSummary = summary,
            Status = "started",
            TimestampUtc = DateTimeOffset.UtcNow,
        };

        this.currentTaskToolCalls.Enqueue(call);
        this.recentToolCallHistory.Enqueue(call);

        while (this.recentToolCallHistory.Count > MaxRecentToolCalls)
        {
            this.recentToolCallHistory.TryDequeue(out _);
        }
    }

    private void OnAssistantText(AssistantTextDto dto)
    {
        this.LastActivityAt = DateTimeOffset.UtcNow;
        this.assistantBuffer += dto.Delta;
    }

    private void OnUsage(UsageDto dto)
    {
        this.LastActivityAt = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref this.inputTokens, dto.InputTokens);
        Interlocked.Exchange(ref this.outputTokens, dto.OutputTokens);
    }

    private void OnStreamProgress(StreamProgressDto dto)
    {
        // The previously-missing liveness pulse: while coda streams an LLM response it emits no
        // assistantText/usage/toolCall, so the watchdog used to be blind to a live-but-slow call.
        // Each pulse now demonstrably proves the session is alive and records what it is doing.
        var now = DateTimeOffset.UtcNow;
        this.LastActivityAt = now;
        Interlocked.Exchange(ref this.lastStreamTicks, now.UtcDateTime.Ticks);
        Interlocked.Exchange(ref this.streamChunks, dto.Chunks);
        Interlocked.Exchange(ref this.streamChars, dto.Chars);

        var streaming = !string.Equals(dto.Phase, "complete", StringComparison.Ordinal);
        Interlocked.Exchange(ref this.isStreaming, streaming ? 1 : 0);

        lock (this.stateLock)
        {
            this.currentActivity = streaming
                ? $"streaming LLM response ({dto.Chars} chars, {dto.Chunks} chunks)"
                : null;
        }
    }

    private Task<bool> HandlePermissionRequestAsync(PermissionDto dto)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingRequests[requestId] = new PendingRequest.Permission(tcs);

        lock (this.stateLock)
        {
            this.State = CodingSessionState.AwaitingPermission;
            this.LastActivityAt = DateTimeOffset.UtcNow;
        }

        this.PermissionRequested?.Invoke(new CodaPermissionRequestEvent(
            this.SessionId,
            requestId,
            dto.ToolName,
            dto.InputPreview));

        return tcs.Task;
    }

    private Task<string> HandleQuestionRequestAsync(QuestionDto dto)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingRequests[requestId] = new PendingRequest.Question(tcs);

        lock (this.stateLock)
        {
            this.State = CodingSessionState.AwaitingQuestion;
            this.LastActivityAt = DateTimeOffset.UtcNow;
        }

        this.Question?.Invoke(new CodaQuestionEvent(
            this.SessionId,
            requestId,
            dto.Question,
            dto.Options,
            dto.MultiSelect));

        return tcs.Task;
    }

    private Task<bool> HandlePlanApprovalRequestAsync(PlanDto dto)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingRequests[requestId] = new PendingRequest.Plan(tcs);

        lock (this.stateLock)
        {
            this.State = CodingSessionState.AwaitingPlan;
            this.LastActivityAt = DateTimeOffset.UtcNow;
        }

        this.PlanApproval?.Invoke(new CodaPlanApprovalEvent(
            this.SessionId,
            requestId,
            dto.Plan));

        return tcs.Task;
    }

    /// <summary>
    /// Transitions the session to <see cref="CodingSessionState.Crashed"/> and records the error
    /// message, WITHOUT raising the <see cref="Error"/> event. The watchdog uses this (it raises
    /// the distinct <see cref="Stalled"/> event instead) so a stall is relayed exactly once.
    /// </summary>
    private void MarkCrashed(string message)
    {
        lock (this.stateLock)
        {
            if (this.State != CodingSessionState.Ended)
            {
                this.State = CodingSessionState.Crashed;
            }

            this.currentTaskId = null;
        }

        this.LastError = message;
    }

    private void RaiseError(int? exitCode, string? stderrTail, string message)
    {
        this.MarkCrashed(message);
        this.LogSessionError(this.SessionId, exitCode, message);
        this.Error?.Invoke(new CodaErrorEvent(this.SessionId, exitCode, stderrTail, message));
    }

    // -----------------------------------------------------------------------
    // Process exit (production path only)
    // -----------------------------------------------------------------------

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process proc)
        {
            return;
        }

        CodingSessionState previousState;
        lock (this.stateLock)
        {
            previousState = this.State;
            if (this.State != CodingSessionState.Ended)
            {
                this.State = previousState == CodingSessionState.Idle
                    ? CodingSessionState.Ended
                    : CodingSessionState.Crashed;
            }
        }

        var stderrTail = this.GetStderrTail();
        var exitCode = TryGetExitCode(proc);

        if (previousState is CodingSessionState.Working
            or CodingSessionState.AwaitingPermission
            or CodingSessionState.AwaitingQuestion
            or CodingSessionState.AwaitingPlan)
        {
            var code = exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            var message = $"coda exited unexpectedly (code={code})";
            this.LastError = message;
            this.LogSessionError(this.SessionId, exitCode, message);
            this.Error?.Invoke(new CodaErrorEvent(
                this.SessionId,
                exitCode,
                stderrTail,
                message));
        }

        this.LogSessionExited(this.SessionId, exitCode);
    }

    // -----------------------------------------------------------------------
    // Stderr reader (production path only)
    // -----------------------------------------------------------------------

    private async Task ReadStderrLoopAsync(Process proc, CancellationToken token)
    {
        try
        {
            using var reader = proc.StandardError;
            string? line;
            while (!token.IsCancellationRequested
                   && (line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
            {
                lock (this.stderrBuffer)
                {
                    if (this.stderrBuffer.Length > 4096)
                    {
                        this.stderrBuffer.Remove(0, this.stderrBuffer.Length - 2048);
                    }

                    this.stderrBuffer.AppendLine(line);
                }

                this.LogCodaStderr(this.SessionId, line);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            this.LogStderrLoopError(ex);
        }
    }

    // -----------------------------------------------------------------------
    // Static helpers
    // -----------------------------------------------------------------------

    private static int? TryGetExitCode(Process proc)
    {
        try
        {
            return proc.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The captured tail of coda's stderr (diagnostics), for surfacing in errors.</summary>
    public string StderrTail => this.GetStderrTail();

    private string GetStderrTail()
    {
        lock (this.stderrBuffer)
        {
            return this.stderrBuffer.ToString();
        }
    }

    /// <summary>Test seam: inject a stderr line without a real subprocess.</summary>
    internal void AppendStderrForTest(string line)
    {
        lock (this.stderrBuffer)
        {
            this.stderrBuffer.AppendLine(line);
        }
    }

    private static string SummarizeInput(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return string.Empty;
        }

        return inputJson.Length <= 120 ? inputJson : inputJson[..117] + "...";
    }

    private static string? Truncate(string? input, int max)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return input.Length <= max ? input : input[..max] + "…";
    }

    // -----------------------------------------------------------------------
    // Pending-request discriminated union
    // -----------------------------------------------------------------------

    private abstract record PendingRequest
    {
        public sealed record Permission(TaskCompletionSource<bool> Tcs) : PendingRequest;

        public sealed record Question(TaskCompletionSource<string> Tcs) : PendingRequest;

        public sealed record Plan(TaskCompletionSource<bool> Tcs) : PendingRequest;
    }

    // -----------------------------------------------------------------------
    // Logger messages
    // -----------------------------------------------------------------------

    [LoggerMessage(EventId = 9200, Level = LogLevel.Information,
        Message = "Coda session started: {sessionId} (assigned={assignedSessionId}) folder={workingFolder} policy={policy}")]
    private partial void LogSessionStarted(string sessionId, string assignedSessionId, string workingFolder, CodingPolicy policy);

    [LoggerMessage(EventId = 9201, Level = LogLevel.Information,
        Message = "Coda session exited: {sessionId} exitCode={exitCode}")]
    private partial void LogSessionExited(string sessionId, int? exitCode);

    [LoggerMessage(EventId = 9202, Level = LogLevel.Warning,
        Message = "Coda session stderr read loop failed")]
    private partial void LogStderrLoopError(Exception ex);

    [LoggerMessage(EventId = 9203, Level = LogLevel.Warning,
        Message = "Coda session dispose error")]
    private partial void LogDisposeError(Exception ex);

    [LoggerMessage(EventId = 9204, Level = LogLevel.Warning,
        Message = "Coda session interrupt failed")]
    private partial void LogInterruptError(Exception ex);

    [LoggerMessage(EventId = 9205, Level = LogLevel.Warning,
        Message = "Coda session shutdown failed")]
    private partial void LogShutdownError(Exception ex);

    [LoggerMessage(EventId = 9206, Level = LogLevel.Warning,
        Message = "Coda session {sessionId} watchdog fired: frozen for {idleSeconds}s — terminating")]
    private partial void LogWatchdogFrozen(string sessionId, int idleSeconds);

    [LoggerMessage(EventId = 9207, Level = LogLevel.Information,
        Message = "coda[{sessionId}] stderr: {line}")]
    private partial void LogCodaStderr(string sessionId, string line);

    [LoggerMessage(EventId = 9208, Level = LogLevel.Information,
        Message = "Coda session {sessionId} error (exitCode={exitCode}): {message}")]
    private partial void LogSessionError(string sessionId, int? exitCode, string message);
}
