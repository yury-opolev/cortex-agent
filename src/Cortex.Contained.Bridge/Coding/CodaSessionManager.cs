using System.Collections.Concurrent;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Singleton manager for live <see cref="CodaSession"/> processes.
/// Enforces the MaxSessions cap, per-channel session binding, and a Bridge-side
/// allow-always cache.  Forwards per-session events to subscribers.
/// </summary>
public sealed partial class CodaSessionManager : IAsyncDisposable

{
    private readonly ConcurrentDictionary<string, CodaSession> sessions = new();
    private readonly Dictionary<string, CodaSessionMetadata> knownSessions = new(StringComparer.Ordinal);
    private readonly Lock metaLock = new();

    // requestId → sessionId, so RespondAsync can route without knowing the session upfront.
    private readonly ConcurrentDictionary<string, string> requestToSession = new(StringComparer.Ordinal);

    private readonly AllowAlwaysCache allowAlwaysCache = new();

    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<CodaSessionManager> logger;
    private readonly IOptionsMonitor<CodaOptions> codaOptions;
    private readonly CodingFoldersStore codingFoldersStore;
    private readonly CodaMcpSettingsStore? mcpSettingsStore;

    public CodaSessionManager(
        ILoggerFactory loggerFactory,
        IOptionsMonitor<CodaOptions> codaOptions,
        CodingFoldersStore codingFoldersStore,
        CodaMcpSettingsStore? mcpSettingsStore = null)
    {
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<CodaSessionManager>();
        this.codaOptions = codaOptions;
        this.codingFoldersStore = codingFoldersStore;
        this.mcpSettingsStore = mcpSettingsStore;
    }

    // -----------------------------------------------------------------------
    // Events (FinalResult / Error / PermissionRequested / Question / PlanApproval)
    // -----------------------------------------------------------------------

    public event Action<CodaFinalResultEvent>? FinalResult;
    public event Action<CodaErrorEvent>? Error;
    public event Action<CodaStalledEvent>? Stalled;
    public event Action<CodaLimitReachedEvent>? LimitReached;
    public event Action<CodaPermissionRequestEvent>? PermissionRequested;
    public event Action<CodaQuestionEvent>? Question;
    public event Action<CodaPlanApprovalEvent>? PlanApproval;

    public CodaOptions Options => this.codaOptions.CurrentValue;

    /// <summary>
    /// Test seam: registers a pre-built (non-started) session directly into the live map so the
    /// per-tenant cap can be exercised without spawning a coda process. Not used in production.
    /// </summary>
    internal void RegisterSessionForTesting(CodaSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.sessions[session.SessionId] = session;
    }

    /// <summary>
    /// Test seam: registers a pre-built (non-started) session into the live map exactly the way
    /// a successful <see cref="StartAsync"/> would — wiring its events (so a later crash updates
    /// the metadata <c>State</c>) and recording its metadata — without spawning a coda process.
    /// Not used in production.
    /// </summary>
    internal void RegisterStartedSessionForTesting(
        CodaSession session,
        string channelId,
        string workingFolder,
        CodingPolicy policy,
        string tenantId)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.WireSessionEvents(session);
        this.sessions[session.SessionId] = session;
        this.RememberMetadata(session.SessionId, channelId, workingFolder, policy, sessionName: null, tenantId);
    }

    /// <summary>
    /// Test seam: drops a session from the live map only, leaving its <c>knownSessions</c> metadata
    /// in place — emulating a crash-cleanup/teardown path that removes the live session without
    /// going through <see cref="EndAsync"/>. Exercises the metadata-fallback liveness path.
    /// Not used in production.
    /// </summary>
    internal void RemoveLiveSessionForTesting(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        this.sessions.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Test seam: inserts a minimal metadata entry so that <see cref="GetSessionTenantId"/> can
    /// return the tenant id even after the live session has been removed from the map.
    /// Not used in production — production code calls <see cref="RememberMetadata"/> via
    /// <see cref="StartAsync"/> / <see cref="ResumeAsync"/>.
    /// </summary>
    internal void RegisterMetadataForTesting(string sessionId, string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        lock (this.metaLock)
        {
            if (!this.knownSessions.ContainsKey(sessionId))
            {
                this.knownSessions[sessionId] = new CodaSessionMetadata(
                    sessionId,
                    ChannelId: string.Empty,
                    WorkingFolder: string.Empty,
                    Policy: CodingPolicy.Prompt,
                    SessionName: null,
                    State: CodingSessionState.Idle,
                    CreatedAt: DateTimeOffset.UtcNow,
                    LastActivityAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId);
            }
        }
    }

    /// <summary>
    /// Returns the tenant id that owns <paramref name="sessionId"/>.
    /// Checks the live session map first; falls back to the persisted
    /// <c>knownSessions</c> metadata for sessions that have already ended.
    /// Returns <c>null</c> when the session is entirely unknown.
    /// </summary>
    internal string? GetSessionTenantId(string sessionId)
    {
        if (this.sessions.TryGetValue(sessionId, out var session))
        {
            return session.TenantId;
        }

        lock (this.metaLock)
        {
            if (this.knownSessions.TryGetValue(sessionId, out var meta))
            {
                return meta.TenantId;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a <see cref="CodaOptions"/> with the MCP policy resolved via the UI store
    /// (coda-mcp.json), falling back to the live YAML options for everything else.
    /// </summary>
    internal CodaOptions EffectiveOptions()
    {
        // Clone so every other field passes through unchanged; only resolved fields are overridden.
        var effective = this.codaOptions.CurrentValue.Clone();

        // The UI store (coda-mcp.json) overrides the cortex.yml Coding:Coda:Mcp policy when set.
        if (this.mcpSettingsStore?.Get() is { } mcp)
        {
            if (mcp.Mcp is { } policy)
            {
                effective.Mcp = policy;
            }

            if (!string.IsNullOrWhiteSpace(mcp.CuratedMcpDir))
            {
                effective.CuratedMcpDir = mcp.CuratedMcpDir;
            }
        }

        return effective;
    }

    public bool IsFolderAllowed(string absolutePath) => this.codingFoldersStore.IsAllowed(absolutePath);

    public IReadOnlyList<CodingFolderEntry> ListFolders() => this.codingFoldersStore.Get();

    // -----------------------------------------------------------------------
    // StartAsync
    // -----------------------------------------------------------------------

    public async Task<CodingStatus> StartAsync(string tenantId, CodingStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(request);

        if (!Directory.Exists(request.WorkingFolder))
        {
            throw new CodingAgentException(CodingAgentErrorCodes.FolderNotFound, $"Folder not found: {request.WorkingFolder}");
        }

        // Hard allowlist gate — the folder must be in the configured list (exact or child).
        if (!this.codingFoldersStore.IsAllowed(request.WorkingFolder))
        {
            throw new CodingAgentException(
                CodingAgentErrorCodes.FolderNotAllowed,
                $"Folder '{request.WorkingFolder}' is not in the allowed list. Add it in the Bridge web UI > Settings > Coding.");
        }

        // Derive the per-folder ceiling from the store.
        var ceiling = this.codingFoldersStore.GetCeiling(request.WorkingFolder);

        // Derive the requested policy: explicit RequestedPolicy takes precedence over the legacy Yolo bool.
        var requested = request.RequestedPolicy ?? (request.Yolo ? CodingPolicy.Yolo : (CodingPolicy?)null);

        // Validate requested policy against ceiling.
        var (policy, policyError) = CodaSessionPolicy.Resolve(requested, ceiling);
        if (policyError is { } err)
        {
            throw new CodingAgentException(err.ErrorCode, err.Message);
        }

        if (CodaSessionAdmission.CheckTenantCeiling(
                this.sessions.Values.Select(s => s.TenantId), tenantId, this.Options.MaxSessions) is { } capError)
        {
            throw new CodingAgentException(capError.ErrorCode, capError.Message);
        }

        var effective = this.EffectiveOptions();

        var sessionId = Guid.NewGuid().ToString("D");
        var session = new CodaSession(
            sessionId,
            request.ChannelId,
            request.WorkingFolder,
            policy,
            effective,
            this.loggerFactory.CreateLogger<CodaSession>(),
            request.Goal,
            request.SessionMemory);

        session.TenantId = tenantId;
        this.WireSessionEvents(session);
        this.sessions[sessionId] = session;
        this.RememberMetadata(sessionId, request.ChannelId, request.WorkingFolder, policy, request.SessionName, tenantId);

        try
        {
            await session.StartAsync(isResume: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var stderrTail = session.StderrTail;
            await this.TeardownFailedStartAsync(sessionId, session).ConfigureAwait(false);
            throw new CodingAgentException(
                CodingAgentErrorCodes.StartTimeout,
                $"coda did not complete startup within {effective.StartTimeoutSeconds}s and was terminated."
                + (string.IsNullOrWhiteSpace(stderrTail) ? "" : $" Last output: {stderrTail.Trim()}"));
        }
        catch (CodingAgentException)
        {
            await this.TeardownFailedStartAsync(sessionId, session).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var stderrTail = session.StderrTail;
            await this.TeardownFailedStartAsync(sessionId, session).ConfigureAwait(false);
            throw new CodingAgentException(
                CodingAgentErrorCodes.StartFailed,
                $"coda failed to start: {ex.Message}"
                + (string.IsNullOrWhiteSpace(stderrTail) ? "" : $" Last output: {stderrTail.Trim()}")
                + " No session is running.");
        }

        return this.SnapshotSession(session);
    }

    /// <summary>
    /// Removes a session that failed to start from both the live map and the metadata index
    /// (marked <see cref="CodingSessionState.Crashed"/>), and disposes it (killing the process
    /// tree). Guarantees no phantom session is ever left behind after a start failure.
    /// </summary>
    private async Task TeardownFailedStartAsync(string sessionId, CodaSession session)
    {
        this.sessions.TryRemove(sessionId, out _);
        lock (this.metaLock)
        {
            if (this.knownSessions.TryGetValue(sessionId, out var meta))
            {
                this.knownSessions[sessionId] = meta with { State = CodingSessionState.Crashed };
            }
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // ResumeAsync
    // -----------------------------------------------------------------------

    public async Task<CodingStatus> ResumeAsync(string tenantId, CodingResumeRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(request);

        if (!Directory.Exists(request.WorkingFolder))
        {
            throw new CodingAgentException(CodingAgentErrorCodes.FolderNotFound, $"Folder not found: {request.WorkingFolder}");
        }

        // Hard allowlist gate — the folder must be in the configured list (exact or child).
        if (!this.codingFoldersStore.IsAllowed(request.WorkingFolder))
        {
            throw new CodingAgentException(
                CodingAgentErrorCodes.FolderNotAllowed,
                $"Folder '{request.WorkingFolder}' is not in the allowed list. Add it in the Bridge web UI > Settings > Coding.");
        }

        var ceiling = this.codingFoldersStore.GetCeiling(request.WorkingFolder);

        // Derive the requested policy: explicit RequestedPolicy takes precedence over the legacy Yolo bool.
        var requested = request.RequestedPolicy ?? (request.Yolo ? CodingPolicy.Yolo : (CodingPolicy?)null);

        var (policy, policyError) = CodaSessionPolicy.Resolve(requested, ceiling);
        if (policyError is { } err)
        {
            throw new CodingAgentException(err.ErrorCode, err.Message);
        }

        if (this.sessions.TryGetValue(request.SessionId, out var existing) && existing.State != CodingSessionState.Ended)
        {
            return this.SnapshotSession(existing);
        }

        if (CodaSessionAdmission.CheckTenantCeiling(
                this.sessions.Values.Select(s => s.TenantId), tenantId, this.Options.MaxSessions) is { } capError)
        {
            throw new CodingAgentException(capError.ErrorCode, capError.Message);
        }

        var effective = this.EffectiveOptions();

        var session = new CodaSession(
            request.SessionId,
            request.ChannelId,
            request.WorkingFolder,
            policy,
            effective,
            this.loggerFactory.CreateLogger<CodaSession>());

        session.TenantId = tenantId;
        this.WireSessionEvents(session);
        this.sessions[request.SessionId] = session;
        this.RememberMetadata(request.SessionId, request.ChannelId, request.WorkingFolder, policy, sessionName: null, tenantId);

        try
        {
            await session.StartAsync(isResume: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var stderrTail = session.StderrTail;
            await this.TeardownFailedStartAsync(request.SessionId, session).ConfigureAwait(false);
            throw new CodingAgentException(
                CodingAgentErrorCodes.StartTimeout,
                $"coda did not complete startup within {effective.StartTimeoutSeconds}s and was terminated."
                + (string.IsNullOrWhiteSpace(stderrTail) ? "" : $" Last output: {stderrTail.Trim()}"));
        }
        catch (CodingAgentException)
        {
            await this.TeardownFailedStartAsync(request.SessionId, session).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var stderrTail = session.StderrTail;
            await this.TeardownFailedStartAsync(request.SessionId, session).ConfigureAwait(false);
            throw new CodingAgentException(
                CodingAgentErrorCodes.StartFailed,
                $"coda failed to start: {ex.Message}"
                + (string.IsNullOrWhiteSpace(stderrTail) ? "" : $" Last output: {stderrTail.Trim()}")
                + " No session is running.");
        }

        return this.SnapshotSession(session);
    }

    // -----------------------------------------------------------------------
    // SendMessageAsync
    // -----------------------------------------------------------------------

    public async Task<CodingSendResponse> SendMessageAsync(CodingSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.sessions.TryGetValue(request.SessionId, out var session))
        {
            throw new CodingAgentException(CodingAgentErrorCodes.NoActiveSession, $"No live session with id {request.SessionId}.");
        }

        if (session.State == CodingSessionState.Working)
        {
            // Mid-turn: try to deliver the message as a steering comment to the running turn so the
            // orchestrator can redirect coda "on the go". If the turn has already ended (race), fall
            // through and deliver it as a normal new turn — a mid-turn message is never silently dropped.
            var (steered, steerTaskId) = await session.TrySteerAsync(request.Message, cancellationToken).ConfigureAwait(false);
            if (steered)
            {
                return new CodingSendResponse
                {
                    TaskId = steerTaskId ?? string.Empty,
                    SessionId = session.SessionId,
                    State = session.State,
                    Steered = true,
                };
            }
        }

        if (session.State != CodingSessionState.Idle)
        {
            // Working here means a steer just raced to turn-end (transient — retry shortly); Awaiting*
            // needs a reply to the pending request first; a dead/not-ready session must not pretend to
            // accept the message.
            var busy = session.State is CodingSessionState.Working
                or CodingSessionState.AwaitingPermission
                or CodingSessionState.AwaitingQuestion
                or CodingSessionState.AwaitingPlan;
            if (busy)
            {
                throw new CodingAgentException(CodingAgentErrorCodes.SessionBusy, $"Session is {session.State}; it is finishing a turn or awaiting your reply. Retry shortly, or respond to the pending request first.");
            }

            throw new CodingAgentException(
                CodingAgentErrorCodes.SessionNotReady,
                $"Session is {session.State} and cannot accept messages. No message was delivered.");
        }

        var taskId = await session.WriteUserMessageAsync(request.Message, cancellationToken).ConfigureAwait(false);
        return new CodingSendResponse
        {
            TaskId = taskId,
            SessionId = session.SessionId,
            State = session.State,
            Steered = false,
        };
    }

    // -----------------------------------------------------------------------
    // RespondAsync — routes to the owning session, handles allow_always
    // -----------------------------------------------------------------------

    public async Task RespondAsync(CodingRespondRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.requestToSession.TryGetValue(request.RequestId, out var sessionId))
        {
            // Stale or unknown request — ignore gracefully.
            this.LogRespondUnknownRequest(request.RequestId);
            return;
        }

        if (!this.sessions.TryGetValue(sessionId, out var session))
        {
            this.LogRespondSessionGone(request.RequestId, sessionId);
            return;
        }

        // For allow_always: record in the cache so the next identical permission auto-approves.
        // We need the tool name; since we don't store it separately in the requestToSession map
        // we derive it from the session's pending requests indirectly — however CodaSession
        // exposes RespondAsync(requestId, response) and fires the result.  The allow-always
        // recording is done BEFORE resolving so we can also look up the toolName.
        // Because CodaSession hides PendingRequests, we store toolName per-request in our own map.
        if (request.Response == "allow_always"
            && this.requestToolNames.TryGetValue(request.RequestId, out var toolName))
        {
            this.allowAlwaysCache.Add(sessionId, toolName);
            this.LogAllowAlways(sessionId, toolName);
        }

        // Clean up the routing maps.
        this.requestToSession.TryRemove(request.RequestId, out _);
        this.requestToolNames.TryRemove(request.RequestId, out _);

        await session.RespondAsync(request.RequestId, request.Response).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // SetGoalAsync — set / update / clear a session's autonomous goal
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sets, updates, or clears a live session's autonomous goal and budget (coda
    /// <c>session/setGoal</c>). A null/empty goal clears it. Throws
    /// <see cref="CodingAgentException"/> with <see cref="CodingAgentErrorCodes.NoActiveSession"/>
    /// when no live session matches. The new goal takes effect from the next message.
    /// </summary>
    public async Task<CodingSetGoalResponse> SetGoalAsync(CodingSetGoalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.sessions.TryGetValue(request.SessionId, out var session))
        {
            throw new CodingAgentException(CodingAgentErrorCodes.NoActiveSession, $"No live session with id {request.SessionId}.");
        }

        try
        {
            var (goal, maxDuration, maxContinuations) = await session
                .SetGoalAsync(request.Goal, request.MaxDuration, request.MaxContinuations, cancellationToken)
                .ConfigureAwait(false);

            return new CodingSetGoalResponse
            {
                SessionId = request.SessionId,
                Goal = goal,
                MaxDuration = maxDuration,
                MaxContinuations = maxContinuations,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodingAgentException(
                CodingAgentErrorCodes.CodaTimeout,
                $"coda did not apply the goal within {this.Options.ControlTimeoutSeconds}s.");
        }
    }

    // -----------------------------------------------------------------------
    // InterruptAsync / EndAsync
    // -----------------------------------------------------------------------

    public async Task<CodingEndResponse> InterruptAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!this.sessions.TryGetValue(sessionId, out var session))
        {
            throw new CodingAgentException(CodingAgentErrorCodes.NoActiveSession, $"No live session with id {sessionId}.");
        }

        var interruptedTaskId = await session.InterruptAsync(cancellationToken).ConfigureAwait(false);
        return new CodingEndResponse
        {
            SessionId = sessionId,
            State = CodingSessionState.Idle,
            InterruptedTaskId = interruptedTaskId,
        };
    }

    /// <summary>
    /// Canonical "stop a session by id" entry point (the <c>coding_session_end</c> agent tool calls
    /// this). Gracefully shuts coda down, kills the process tree (via <see cref="CodaSession.DisposeAsync"/>),
    /// removes the session from the live map, clears its allow-always cache, and marks its
    /// <c>knownSessions</c> metadata <see cref="CodingSessionState.Ended"/> so subsequent
    /// <see cref="GetStatus"/> / <see cref="ListSessions"/> calls report <c>Ended</c> rather than a
    /// live/Idle phantom. Idempotent: calling it for an already-ended or unknown id still returns
    /// <see cref="CodingSessionState.Ended"/>.
    /// </summary>
    public async Task<CodingEndResponse> EndAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (this.sessions.TryRemove(sessionId, out var session))
        {
            this.allowAlwaysCache.ClearSession(sessionId);
            await session.EndAsync(cancellationToken).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        lock (this.metaLock)
        {
            if (this.knownSessions.TryGetValue(sessionId, out var meta))
            {
                this.knownSessions[sessionId] = meta with { State = CodingSessionState.Ended };
            }
        }

        return new CodingEndResponse
        {
            SessionId = sessionId,
            State = CodingSessionState.Ended,
        };
    }

    // -----------------------------------------------------------------------
    // GetHistoryAsync — full transcript or incremental slice since a cursor
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a live session's transcript. When <paramref name="sinceIndex"/> is null the full
    /// history is returned (no <c>nextIndex</c>); otherwise only the messages after that cursor
    /// are returned, with the next cursor. Throws <see cref="CodingAgentException"/> with
    /// <see cref="CodingAgentErrorCodes.NoActiveSession"/> when no live session matches.
    /// </summary>
    public async Task<CodingHistory> GetHistoryAsync(string sessionId, int? sinceIndex, CancellationToken cancellationToken)
    {
        if (!this.sessions.TryGetValue(sessionId, out var session))
        {
            throw new CodingAgentException(CodingAgentErrorCodes.NoActiveSession, $"No live session with id {sessionId}.");
        }

        try
        {
            if (sinceIndex is { } since)
            {
                var (messages, nextIndex) = await session.GetMessagesAsync(since, cancellationToken).ConfigureAwait(false);
                return new CodingHistory
                {
                    Messages = ToHistoryMessages(messages),
                    NextIndex = nextIndex,
                };
            }

            var full = await session.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
            return new CodingHistory
            {
                Messages = ToHistoryMessages(full),
                NextIndex = null,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-op ControlTimeout fired: surface a definitive coda_timeout rather than
            // letting a bare cancellation reach the agent as a generic internal_error.
            throw new CodingAgentException(
                CodingAgentErrorCodes.CodaTimeout,
                $"coda did not return session history within {this.Options.ControlTimeoutSeconds}s.");
        }
    }

    private static List<CodingHistoryMessage> ToHistoryMessages(IReadOnlyList<HistoryMessageDto> messages)
    {
        return messages
            .Select(m => new CodingHistoryMessage { Role = m.Role, Content = m.Content })
            .ToList();
    }

    // -----------------------------------------------------------------------
    // GetStatus / ListSessions
    // -----------------------------------------------------------------------

    public CodingStatus? GetStatus(string sessionId)
    {
        if (this.sessions.TryGetValue(sessionId, out var session))
        {
            return this.SnapshotSession(session);
        }

        lock (this.metaLock)
        {
            if (this.knownSessions.TryGetValue(sessionId, out var meta))
            {
                return new CodingStatus
                {
                    SessionId = sessionId,
                    ChannelId = meta.ChannelId,
                    WorkingFolder = meta.WorkingFolder,
                    State = meta.State,
                    Policy = meta.Policy,
                    SessionName = meta.SessionName,
                    CreatedAt = meta.CreatedAt,
                    LastActivityAt = meta.LastActivityAt,
                };
            }
        }

        return null;
    }

    public IReadOnlyList<CodingStatus> ListSessions()
    {
        var live = this.sessions.Values.Select(this.SnapshotSession).ToList();
        var liveIds = live.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);

        lock (this.metaLock)
        {
            foreach (var (id, meta) in this.knownSessions)
            {
                if (liveIds.Contains(id))
                {
                    continue;
                }

                live.Add(new CodingStatus
                {
                    SessionId = id,
                    ChannelId = meta.ChannelId,
                    WorkingFolder = meta.WorkingFolder,
                    State = meta.State,
                    Policy = meta.Policy,
                    SessionName = meta.SessionName,
                    CreatedAt = meta.CreatedAt,
                    LastActivityAt = meta.LastActivityAt,
                });
            }
        }

        return live;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, session) in this.sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        this.sessions.Clear();
    }

    // -----------------------------------------------------------------------
    // Private: requestId → toolName map (for allow_always recording)
    // -----------------------------------------------------------------------

    private readonly ConcurrentDictionary<string, string> requestToolNames = new(StringComparer.Ordinal);

    // -----------------------------------------------------------------------
    // Private: wire session events
    // -----------------------------------------------------------------------

    private void WireSessionEvents(CodaSession session)
    {
        session.FinalResult += evt =>
        {
            this.UpdateMetadataLastActivity(evt.SessionId);
            this.FinalResult?.Invoke(evt);
        };
        session.Error += evt =>
        {
            // A crash must make the metadata State truthful so the knownSessions fallback in
            // GetStatus/ListSessions never reports a dead session as Idle after it leaves the
            // live map (e.g. via a future cleanup/teardown).
            this.UpdateMetadataState(evt.SessionId, CodingSessionState.Crashed);
            this.UpdateMetadataLastActivity(evt.SessionId);
            this.Error?.Invoke(evt);
        };
        session.Stalled += evt =>
        {
            // A stall is terminal (the watchdog kills the process), so make the metadata truthful
            // exactly as the Error path does, then forward the stall-specific signal so cortex can
            // relay status=stalled and resume rather than treat the task as a hard failure.
            this.UpdateMetadataState(evt.SessionId, CodingSessionState.Crashed);
            this.UpdateMetadataLastActivity(evt.SessionId);
            this.Stalled?.Invoke(evt);
        };
        session.LimitReached += evt =>
        {
            // Recoverable soft stop — do NOT mark the metadata Crashed (unlike Error/Stalled). The
            // session returns to Idle via the turnComplete that immediately follows; just refresh
            // activity and forward the distinct signal so the agent can offer to continue the run.
            this.UpdateMetadataLastActivity(evt.SessionId);
            this.LimitReached?.Invoke(evt);
        };
        session.PermissionRequested += evt =>
        {
            // Check the allow-always cache first — if the tool was previously approved for
            // this session, auto-allow without raising the event.
            if (this.allowAlwaysCache.IsAllowed(evt.SessionId, evt.ToolName))
            {
                this.LogAutoAllowed(evt.SessionId, evt.ToolName);
                // Resolve the session's parked TCS directly.
                _ = session.RespondAsync(evt.RequestId, "allow_always");
                return;
            }

            // Park the routing info so RespondAsync can find it later.
            this.requestToSession[evt.RequestId] = evt.SessionId;
            this.requestToolNames[evt.RequestId] = evt.ToolName;

            this.PermissionRequested?.Invoke(evt);
        };
        session.Question += evt =>
        {
            this.requestToSession[evt.RequestId] = evt.SessionId;
            this.Question?.Invoke(evt);
        };
        session.PlanApproval += evt =>
        {
            this.requestToSession[evt.RequestId] = evt.SessionId;
            this.PlanApproval?.Invoke(evt);
        };
    }

    private CodingStatus SnapshotSession(CodaSession session)
    {
        return new CodingStatus
        {
            SessionId = session.SessionId,
            ChannelId = session.ChannelId,
            WorkingFolder = session.WorkingFolder,
            State = session.State,
            Policy = session.Policy,
            SessionName = null,
            CreatedAt = session.CreatedAt,
            LastActivityAt = session.LastActivityAt,
            CurrentTaskId = session.CurrentTaskId,
            LastUserMessage = session.LastUserMessage,
            LastAssistantSummary = session.LastAssistantSummary,
            LastToolCalls = session.RecentToolCalls,
            TelemetryLogPath = session.TelemetryLogPath,
            LastError = session.LastError,
            InputTokens = session.InputTokens,
            OutputTokens = session.OutputTokens,
            IsStreaming = session.IsStreaming,
            StreamedChars = session.StreamedChars,
            StreamedChunks = session.StreamedChunks,
            LastStreamActivityAt = session.LastStreamActivityAt,
            CurrentActivity = session.CurrentActivity,
            GoalStatus = session.GoalStatus,
        };
    }

    private void RememberMetadata(
        string sessionId,
        string channelId,
        string workingFolder,
        CodingPolicy policy,
        string? sessionName,
        string? tenantId)
    {
        lock (this.metaLock)
        {
            this.knownSessions[sessionId] = new CodaSessionMetadata(
                sessionId,
                channelId,
                workingFolder,
                policy,
                sessionName,
                CodingSessionState.Idle,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                tenantId);
        }
    }

    private void UpdateMetadataLastActivity(string sessionId)
    {
        lock (this.metaLock)
        {
            if (this.knownSessions.TryGetValue(sessionId, out var meta))
            {
                this.knownSessions[sessionId] = meta with { LastActivityAt = DateTimeOffset.UtcNow };
            }
        }
    }

    private void UpdateMetadataState(string sessionId, CodingSessionState state)
    {
        lock (this.metaLock)
        {
            if (this.knownSessions.TryGetValue(sessionId, out var meta))
            {
                this.knownSessions[sessionId] = meta with { State = state };
            }
        }
    }

    // -----------------------------------------------------------------------
    // Private record
    // -----------------------------------------------------------------------

    private sealed record CodaSessionMetadata(
        string SessionId,
        string ChannelId,
        string WorkingFolder,
        CodingPolicy Policy,
        string? SessionName,
        CodingSessionState State,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastActivityAt,
        string? TenantId);

    // -----------------------------------------------------------------------
    // Logger messages
    // -----------------------------------------------------------------------

    [LoggerMessage(EventId = 9300, Level = LogLevel.Debug,
        Message = "CodaSessionManager: allow-always auto-approved sessionId={sessionId} tool={toolName}")]
    private partial void LogAutoAllowed(string sessionId, string toolName);

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information,
        Message = "CodaSessionManager: recorded allow-always sessionId={sessionId} tool={toolName}")]
    private partial void LogAllowAlways(string sessionId, string toolName);

    [LoggerMessage(EventId = 9302, Level = LogLevel.Warning,
        Message = "CodaSessionManager: respond for unknown requestId={requestId}")]
    private partial void LogRespondUnknownRequest(string requestId);

    [LoggerMessage(EventId = 9303, Level = LogLevel.Warning,
        Message = "CodaSessionManager: respond for requestId={requestId} — session={sessionId} no longer live")]
    private partial void LogRespondSessionGone(string requestId, string sessionId);
}
