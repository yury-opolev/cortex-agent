using Cortex.Contained.Contracts.Coding;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>
/// External-agent (Claude Code) extensions for <see cref="HubClient"/>.
/// Provides:
///   - Inbound callback registration for the 8 <see cref="IAgentHubClient"/> session-management methods.
///   - Outbound calls to the 4 <see cref="IAgentHub"/> push-back methods.
/// </summary>
public sealed partial class HubClient
{
    /// <summary>Agent → Bridge: start an coding agent session.</summary>
    public event Func<CodingStartRequest, Task<CodingStatus>>? OnStartCodingSession;

    /// <summary>Agent → Bridge: resume an existing coding agent session.</summary>
    public event Func<CodingResumeRequest, Task<CodingStatus>>? OnResumeCodingSession;

    /// <summary>Agent → Bridge: send a user message to a session.</summary>
    public event Func<CodingSendRequest, Task<CodingSendResponse>>? OnSendCodingMessage;

    /// <summary>Agent → Bridge: respond to a permission/clarification ask.</summary>
    public event Func<CodingRespondRequest, Task>? OnRespondCodingPrompt;

    /// <summary>Agent → Bridge: set/update/clear a session's autonomous goal.</summary>
    public event Func<CodingSetGoalRequest, Task<CodingSetGoalResponse>>? OnSetCodingGoal;

    /// <summary>Agent → Bridge: interrupt the current task.</summary>
    public event Func<string, Task<CodingEndResponse>>? OnInterruptCodingSession;

    /// <summary>Agent → Bridge: end the session.</summary>
    public event Func<string, Task<CodingEndResponse>>? OnEndCodingSession;

    /// <summary>Agent → Bridge: get session status.</summary>
    public event Func<string, Task<CodingStatus?>>? OnGetCodingStatus;

    /// <summary>Agent → Bridge: get session transcript (full or incremental since a cursor).</summary>
    public event Func<CodingHistoryRequest, Task<CodingHistory>>? OnGetCodingHistory;

    /// <summary>Agent → Bridge: list all sessions.</summary>
    public event Func<Task<CodingSessionList>>? OnListCodingSessions;

    /// <summary>Agent → Bridge: query whether a folder is in the coding folder allowlist.</summary>
    public event Func<CodingFolderQueryRequest, Task<bool>>? OnIsCodingFolderAllowed;

    /// <summary>Agent → Bridge: list all configured allowed coding folders.</summary>
    public event Func<Task<CodingFolderList>>? OnListCodingFolders;

    /// <summary>Bridge → Agent: notify final result.</summary>
    public async Task NotifyCodingFinalResultAsync(CodingFinalResultEvent evt, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(nameof(IAgentHub.NotifyCodingFinalResult), evt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bridge → Agent: notify permission request.</summary>
    public async Task NotifyCodingPermissionRequestAsync(CodingPermissionRequestEvent evt, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(nameof(IAgentHub.NotifyCodingPermissionRequest), evt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bridge → Agent: notify question.</summary>
    public async Task NotifyCodingQuestionAsync(CodingQuestionRequestEvent evt, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(nameof(IAgentHub.NotifyCodingQuestion), evt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bridge → Agent: notify plan-approval request.</summary>
    public async Task NotifyCodingPlanApprovalAsync(CodingPlanApprovalRequestEvent evt, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(nameof(IAgentHub.NotifyCodingPlanApproval), evt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bridge → Agent: notify error/crash.</summary>
    public async Task NotifyCodingErrorAsync(CodingErrorEvent evt, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(nameof(IAgentHub.NotifyCodingError), evt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bridge → Agent: notify a watchdog-detected stall (resumable, distinct from a crash).</summary>
    public async Task NotifyCodingStalledAsync(CodingStalledEvent evt, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(nameof(IAgentHub.NotifyCodingStalled), evt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bridge → Agent: notify a recoverable per-turn limit (max_tokens / iteration cap) — not a crash.</summary>
    public async Task NotifyCodingLimitReachedAsync(CodingLimitReachedEvent evt, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(nameof(IAgentHub.NotifyCodingLimitReached), evt, cancellationToken).ConfigureAwait(false);
    }

    private void RegisterCodingCallbacks(HubConnection connection)
    {
        connection.On<CodingStartRequest, CodingStatus>(
            nameof(IAgentHubClient.StartCodingSession),
            req => this.OnStartCodingSession?.Invoke(req)
                ?? Task.FromException<CodingStatus>(new InvalidOperationException("No coding agent handler registered.")));

        connection.On<CodingResumeRequest, CodingStatus>(
            nameof(IAgentHubClient.ResumeCodingSession),
            req => this.OnResumeCodingSession?.Invoke(req)
                ?? Task.FromException<CodingStatus>(new InvalidOperationException("No coding agent handler registered.")));

        connection.On<CodingSendRequest, CodingSendResponse>(
            nameof(IAgentHubClient.SendCodingMessage),
            req => this.OnSendCodingMessage?.Invoke(req)
                ?? Task.FromException<CodingSendResponse>(new InvalidOperationException("No coding agent handler registered.")));

        connection.On<CodingRespondRequest>(
            nameof(IAgentHubClient.RespondCodingPrompt),
            req => this.OnRespondCodingPrompt?.Invoke(req) ?? Task.CompletedTask);

        connection.On<CodingSetGoalRequest, CodingSetGoalResponse>(
            nameof(IAgentHubClient.SetCodingGoal),
            req => this.OnSetCodingGoal?.Invoke(req)
                ?? Task.FromException<CodingSetGoalResponse>(new InvalidOperationException("No coding agent handler registered.")));

        connection.On<string, CodingEndResponse>(
            nameof(IAgentHubClient.InterruptCodingSession),
            sessionId => this.OnInterruptCodingSession?.Invoke(sessionId)
                ?? Task.FromException<CodingEndResponse>(new InvalidOperationException("No coding agent handler registered.")));

        connection.On<string, CodingEndResponse>(
            nameof(IAgentHubClient.EndCodingSession),
            sessionId => this.OnEndCodingSession?.Invoke(sessionId)
                ?? Task.FromException<CodingEndResponse>(new InvalidOperationException("No coding agent handler registered.")));

        connection.On<string, CodingStatus?>(
            nameof(IAgentHubClient.GetCodingStatus),
            sessionId => this.OnGetCodingStatus?.Invoke(sessionId)
                ?? Task.FromResult<CodingStatus?>(null));

        connection.On<CodingHistoryRequest, CodingHistory>(
            nameof(IAgentHubClient.GetCodingHistory),
            req => this.OnGetCodingHistory?.Invoke(req)
                ?? Task.FromResult(new CodingHistory()));

        connection.On<CodingSessionList>(
            nameof(IAgentHubClient.ListCodingSessions),
            () => this.OnListCodingSessions?.Invoke()
                ?? Task.FromResult(new CodingSessionList { Sessions = [] }));

        connection.On<CodingFolderQueryRequest, bool>(
            nameof(IAgentHubClient.IsCodingFolderAllowed),
            req => this.OnIsCodingFolderAllowed?.Invoke(req) ?? Task.FromResult(false));

        connection.On<CodingFolderList>(
            nameof(IAgentHubClient.ListCodingFolders),
            () => this.OnListCodingFolders?.Invoke()
                ?? Task.FromResult(new CodingFolderList { Folders = [] }));
    }
}
