using System.Collections.Concurrent;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Coding;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Wires per-tenant <see cref="HubClient"/> external-agent callbacks to the local
/// <see cref="CodaSessionManager"/>, and forwards manager events back to the owning
/// tenant's connected agent hubs. Each outbound event is routed exclusively to the
/// tenant that owns the session (<see cref="CodaSession.TenantId"/>), preventing
/// cross-tenant event leakage.
/// </summary>
public sealed partial class CodingHubBinder
{
    private readonly CodaSessionManager manager;
    private readonly ILogger<CodingHubBinder> logger;

    /// <summary>
    /// Maps each wired <see cref="HubClient"/> to the tenant id it represents.
    /// Used by <see cref="SelectTargets"/> to route outbound events only to the
    /// owning tenant's client(s).
    /// </summary>
    private readonly ConcurrentDictionary<HubClient, string> wiredClients = new();

    private bool eventsWired;
    private readonly Lock eventsLock = new();

    public CodingHubBinder(
        CodaSessionManager manager,
        ILogger<CodingHubBinder> logger)
    {
        this.manager = manager;
        this.logger = logger;
    }

    /// <summary>
    /// Wire a tenant's <see cref="HubClient"/> for external-agent traffic.
    /// Called from <c>Worker.OnTenantClientConnected</c> for each tenant.
    /// </summary>
    public void WireHubClient(HubClient client, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!this.wiredClients.TryAdd(client, tenantId))
        {
            return;
        }

        // Inbound: Agent → Bridge calls. Start/resume carry the tenant id for the per-tenant cap.
        // Every handler is wrapped so a CodingAgentException's stable code survives the SignalR
        // boundary (only a HubException's message is guaranteed to reach the Agent Host).
        client.OnStartCodingSession += req => Guard(() => this.manager.StartAsync(tenantId, req, CancellationToken.None));
        client.OnResumeCodingSession += req => Guard(() => this.manager.ResumeAsync(tenantId, req, CancellationToken.None));
        client.OnSendCodingMessage += this.HandleSend;
        client.OnRespondCodingPrompt += this.HandleRespond;
        client.OnSetCodingGoal += this.HandleSetGoal;
        client.OnInterruptCodingSession += this.HandleInterrupt;
        client.OnEndCodingSession += this.HandleEnd;
        client.OnGetCodingStatus += this.HandleGetStatus;
        client.OnGetCodingHistory += this.HandleGetHistory;
        client.OnListCodingSessions += this.HandleListSessions;
        client.OnIsCodingFolderAllowed += this.HandleIsFolderAllowed;
        client.OnListCodingFolders += this.HandleListCodingFolders;

        this.EnsureSessionEventsWired();
    }

    private void EnsureSessionEventsWired()
    {
        lock (this.eventsLock)
        {
            if (this.eventsWired)
            {
                return;
            }

            this.manager.FinalResult += this.OnSessionFinalResult;
            this.manager.Error += this.OnSessionError;
            this.manager.Stalled += this.OnSessionStalled;
            this.manager.LimitReached += this.OnSessionLimitReached;
            this.manager.PermissionRequested += this.OnSessionPermissionRequested;
            this.manager.Question += this.OnSessionQuestion;
            this.manager.PlanApproval += this.OnSessionPlanApproval;
            this.eventsWired = true;
        }
    }

    private Task<CodingSendResponse> HandleSend(CodingSendRequest req) =>
        Guard(() => this.manager.SendMessageAsync(req, CancellationToken.None));

    private Task HandleRespond(CodingRespondRequest req) =>
        Guard(() => this.manager.RespondAsync(req, CancellationToken.None));

    private Task<CodingSetGoalResponse> HandleSetGoal(CodingSetGoalRequest req) =>
        Guard(() => this.manager.SetGoalAsync(req, CancellationToken.None));

    private Task<CodingEndResponse> HandleInterrupt(string sessionId) =>
        Guard(() => this.manager.InterruptAsync(sessionId, CancellationToken.None));

    private Task<CodingEndResponse> HandleEnd(string sessionId) =>
        Guard(() => this.manager.EndAsync(sessionId, CancellationToken.None));

    /// <summary>
    /// Runs a manager call and converts a <see cref="CodingAgentException"/> into a
    /// <see cref="HubException"/> whose message wire-encodes the stable error code, so the
    /// Agent Host can decode it back (see <see cref="CodingErrorWire"/>). Without this the code
    /// is lost and the agent only sees an opaque message.
    /// </summary>
    internal static async Task<T> Guard<T>(Func<Task<T>> op)
    {
        try
        {
            return await op().ConfigureAwait(false);
        }
        catch (CodingAgentException ex)
        {
            throw new HubException(CodingErrorWire.Encode(ex.ErrorCode, ex.Message));
        }
    }

    /// <summary>Void-returning overload of <see cref="Guard{T}"/>.</summary>
    internal static async Task Guard(Func<Task> op)
    {
        try
        {
            await op().ConfigureAwait(false);
        }
        catch (CodingAgentException ex)
        {
            throw new HubException(CodingErrorWire.Encode(ex.ErrorCode, ex.Message));
        }
    }

    private Task<CodingStatus?> HandleGetStatus(string sessionId) =>
        Task.FromResult(this.manager.GetStatus(sessionId));

    private Task<CodingHistory> HandleGetHistory(CodingHistoryRequest req) =>
        Guard(() => this.manager.GetHistoryAsync(req.SessionId, req.SinceIndex, CancellationToken.None));

    private Task<CodingSessionList> HandleListSessions() =>
        Task.FromResult(new CodingSessionList { Sessions = this.manager.ListSessions() });

    private Task<bool> HandleIsFolderAllowed(CodingFolderQueryRequest req) =>
        Task.FromResult(this.manager.IsFolderAllowed(req.AbsolutePath));

    private Task<CodingFolderList> HandleListCodingFolders()
    {
        var folders = this.manager.ListFolders()
            .Select(entry => new CodingFolderInfo { AbsolutePath = entry.Path, Label = entry.Label })
            .ToList();
        return Task.FromResult(new CodingFolderList { Folders = folders });
    }

    /// <summary>
    /// Returns the subset of <paramref name="clients"/> that belong to
    /// <paramref name="tenantId"/>. When <paramref name="tenantId"/> is <c>null</c>
    /// (session ownership could not be resolved) ALL clients are returned as a safety
    /// fallback to preserve the pre-isolation broadcast behaviour and avoid dropping events.
    /// Comparison is Ordinal (case-sensitive).
    /// </summary>
    /// <param name="clients">The full wired-client map (client → tenantId).</param>
    /// <param name="tenantId">Owning tenant, or <c>null</c> to broadcast to all.</param>
    internal static IEnumerable<HubClient> SelectTargets(
        IReadOnlyDictionary<HubClient, string> clients,
        string? tenantId)
    {
        if (tenantId is null)
        {
            return clients.Keys;
        }

        return clients
            .Where(kvp => string.Equals(kvp.Value, tenantId, StringComparison.Ordinal))
            .Select(kvp => kvp.Key);
    }

    private void OnSessionFinalResult(CodaFinalResultEvent evt)
    {
        var tenantId = this.ResolveSessionTenant(evt.SessionId);
        var payload = new CodingFinalResultEvent
        {
            SessionId = evt.SessionId,
            TaskId = evt.TaskId,
            FinalText = evt.FinalText,
            ToolCalls = evt.ToolCalls,
        };
        foreach (var client in SelectTargets(this.wiredClients, tenantId))
        {
            _ = this.SafePushAsync(() => client.NotifyCodingFinalResultAsync(payload, CancellationToken.None));
        }
    }

    private void OnSessionError(CodaErrorEvent evt)
    {
        this.LogCodingError(evt.SessionId, evt.ExitCode, evt.Message);
        var tenantId = this.ResolveSessionTenant(evt.SessionId);
        var payload = new CodingErrorEvent
        {
            SessionId = evt.SessionId,
            ExitCode = evt.ExitCode,
            StderrTail = evt.StderrTail,
            Message = evt.Message,
        };
        foreach (var client in SelectTargets(this.wiredClients, tenantId))
        {
            _ = this.SafePushAsync(() => client.NotifyCodingErrorAsync(payload, CancellationToken.None));
        }
    }

    private void OnSessionStalled(CodaStalledEvent evt)
    {
        this.LogCodingStalled(evt.SessionId, evt.IdleSeconds, evt.WasStreaming, evt.Message);
        var tenantId = this.ResolveSessionTenant(evt.SessionId);
        var payload = new CodingStalledEvent
        {
            SessionId = evt.SessionId,
            IdleSeconds = evt.IdleSeconds,
            WasStreaming = evt.WasStreaming,
            StreamedChars = evt.StreamedChars,
            StderrTail = evt.StderrTail,
            Message = evt.Message,
        };
        foreach (var client in SelectTargets(this.wiredClients, tenantId))
        {
            _ = this.SafePushAsync(() => client.NotifyCodingStalledAsync(payload, CancellationToken.None));
        }
    }

    private void OnSessionLimitReached(CodaLimitReachedEvent evt)
    {
        this.LogCodingLimitReached(evt.SessionId, evt.Kind, evt.Message);
        var tenantId = this.ResolveSessionTenant(evt.SessionId);
        var payload = new CodingLimitReachedEvent
        {
            SessionId = evt.SessionId,
            Kind = evt.Kind,
            Message = evt.Message,
        };
        foreach (var client in SelectTargets(this.wiredClients, tenantId))
        {
            _ = this.SafePushAsync(() => client.NotifyCodingLimitReachedAsync(payload, CancellationToken.None));
        }
    }

    private void OnSessionPermissionRequested(CodaPermissionRequestEvent evt)
    {
        var tenantId = this.ResolveSessionTenant(evt.SessionId);
        var payload = new CodingPermissionRequestEvent
        {
            SessionId = evt.SessionId,
            RequestId = evt.RequestId,
            ToolName = evt.ToolName,
            InputPreview = evt.InputPreview,
        };
        foreach (var client in SelectTargets(this.wiredClients, tenantId))
        {
            _ = this.SafePushAsync(() => client.NotifyCodingPermissionRequestAsync(payload, CancellationToken.None));
        }
    }

    private void OnSessionQuestion(CodaQuestionEvent evt)
    {
        var tenantId = this.ResolveSessionTenant(evt.SessionId);
        var payload = new CodingQuestionRequestEvent
        {
            SessionId = evt.SessionId,
            RequestId = evt.RequestId,
            Question = evt.Question,
            Options = evt.Options,
            MultiSelect = evt.MultiSelect,
        };
        foreach (var client in SelectTargets(this.wiredClients, tenantId))
        {
            _ = this.SafePushAsync(() => client.NotifyCodingQuestionAsync(payload, CancellationToken.None));
        }
    }

    private void OnSessionPlanApproval(CodaPlanApprovalEvent evt)
    {
        var tenantId = this.ResolveSessionTenant(evt.SessionId);
        var payload = new CodingPlanApprovalRequestEvent
        {
            SessionId = evt.SessionId,
            RequestId = evt.RequestId,
            Plan = evt.Plan,
        };
        foreach (var client in SelectTargets(this.wiredClients, tenantId))
        {
            _ = this.SafePushAsync(() => client.NotifyCodingPlanApprovalAsync(payload, CancellationToken.None));
        }
    }

    /// <summary>
    /// Resolves the owning tenant for <paramref name="sessionId"/> via the manager lookup.
    /// Logs a warning and returns <c>null</c> when the session is unknown (stale/ended without
    /// metadata), which causes <see cref="SelectTargets"/> to fall back to broadcast-all so
    /// no event is ever silently dropped.
    /// </summary>
    private string? ResolveSessionTenant(string sessionId)
    {
        var tenantId = this.manager.GetSessionTenantId(sessionId);
        if (tenantId is null)
        {
            this.LogUnresolvedSessionTenant(sessionId);
        }

        return tenantId;
    }

    private async Task SafePushAsync(Func<Task> push)
    {
        try
        {
            await push().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogPushFailed(ex);
        }
    }

    [LoggerMessage(EventId = 9110, Level = LogLevel.Warning, Message = "Failed to push external-agent event to Agent Host")]
    private partial void LogPushFailed(Exception ex);

    [LoggerMessage(EventId = 9111, Level = LogLevel.Warning,
        Message = "CodingHubBinder: could not resolve owning tenant for sessionId={sessionId} — broadcasting to all clients as fallback")]
    private partial void LogUnresolvedSessionTenant(string sessionId);

    [LoggerMessage(EventId = 9112, Level = LogLevel.Information,
        Message = "Coding session {sessionId} error relayed to agent (exitCode={exitCode}): {message}")]
    private partial void LogCodingError(string sessionId, int? exitCode, string message);

    [LoggerMessage(EventId = 9113, Level = LogLevel.Information,
        Message = "Coding session {sessionId} stall relayed to agent (idle={idleSeconds}s, streaming={wasStreaming}): {message}")]
    private partial void LogCodingStalled(string sessionId, int idleSeconds, bool wasStreaming, string message);

    [LoggerMessage(EventId = 9114, Level = LogLevel.Information,
        Message = "Coding session {sessionId} hit a recoverable limit ({kind}) relayed to agent: {message}")]
    private partial void LogCodingLimitReached(string sessionId, string kind, string message);
}
