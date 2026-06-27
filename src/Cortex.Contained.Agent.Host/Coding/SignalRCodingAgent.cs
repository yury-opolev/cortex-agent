using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Coding;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// MVP <see cref="ICodingAgent"/> implementation that forwards every call to the
/// connected Bridge via the existing <see cref="IAgentHubClient"/> SignalR proxy.
/// Every invoke is bounded by <see cref="CodingAgentOptions.BridgeInvokeTimeoutSeconds"/>
/// so an unresponsive Bridge can never hold the per-channel lock forever.
/// </summary>
public sealed class SignalRCodingAgent : ICodingAgent
{
    private readonly IBridgeClientProvider bridgeClient;
    private readonly TimeSpan invokeTimeout;

    public SignalRCodingAgent(IBridgeClientProvider bridgeClient, CodingAgentOptions options)
    {
        this.bridgeClient = bridgeClient;
        this.invokeTimeout = TimeSpan.FromSeconds(Math.Max(1, options.BridgeInvokeTimeoutSeconds));
    }

    private IAgentHubClient Client =>
        this.bridgeClient.Client ?? throw new InvalidOperationException("Bridge is not connected.");

    public Task<CodingStatus> StartSessionAsync(CodingStartRequest request, CancellationToken cancellationToken) =>
        this.InvokeAsync(c => c.StartCodingSession(request), cancellationToken);

    public Task<CodingStatus> ResumeSessionAsync(CodingResumeRequest request, CancellationToken cancellationToken) =>
        this.InvokeAsync(c => c.ResumeCodingSession(request), cancellationToken);

    public Task<CodingSendResponse> SendMessageAsync(CodingSendRequest request, CancellationToken cancellationToken) =>
        this.InvokeAsync(c => c.SendCodingMessage(request), cancellationToken);

    public Task RespondAsync(CodingRespondRequest request, CancellationToken cancellationToken) =>
        this.InvokeAsync(c => c.RespondCodingPrompt(request), cancellationToken);

    public Task<CodingSetGoalResponse> SetGoalAsync(CodingSetGoalRequest request, CancellationToken cancellationToken) =>
        this.InvokeAsync(c => c.SetCodingGoal(request), cancellationToken);

    public Task<CodingEndResponse> InterruptAsync(string sessionId, CancellationToken cancellationToken) =>
        this.InvokeAsync(c => c.InterruptCodingSession(sessionId), cancellationToken);

    public Task<CodingEndResponse> EndSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        this.InvokeAsync(c => c.EndCodingSession(sessionId), cancellationToken);

    public Task<CodingStatus?> GetStatusAsync(string sessionId, CancellationToken cancellationToken) =>
        this.InvokeAsync(c => c.GetCodingStatus(sessionId), cancellationToken);

    public Task<CodingHistory> GetHistoryAsync(string sessionId, int? sinceIndex, CancellationToken cancellationToken) =>
        this.InvokeAsync(
            c => c.GetCodingHistory(new CodingHistoryRequest { SessionId = sessionId, SinceIndex = sinceIndex }),
            cancellationToken);

    public async Task<IReadOnlyList<CodingStatus>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var list = await this.InvokeAsync(c => c.ListCodingSessions(), cancellationToken).ConfigureAwait(false);
        return list.Sessions;
    }

    public Task<bool> IsFolderAllowedAsync(string absolutePath, CancellationToken cancellationToken) =>
        this.InvokeAsync(
            c => c.IsCodingFolderAllowed(new CodingFolderQueryRequest { AbsolutePath = absolutePath }),
            cancellationToken);

    public Task<CodingFolderList> ListAllowedFoldersAsync(CancellationToken cancellationToken) =>
        this.InvokeAsync(c => c.ListCodingFolders(), cancellationToken);

    private async Task<T> InvokeAsync<T>(Func<IAgentHubClient, Task<T>> call, CancellationToken cancellationToken)
    {
        var client = this.Client; // throws InvalidOperationException if Bridge not connected
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(this.invokeTimeout);
        try
        {
            return await call(client).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CodingInvokeException.Unreachable((int)this.invokeTimeout.TotalSeconds);
        }
        catch (Exception ex) when (CodingErrorWire.TryDecode(ex.Message, out var code, out var message))
        {
            throw CodingInvokeException.FromWire(code, message);
        }
    }

    private async Task InvokeAsync(Func<IAgentHubClient, Task> call, CancellationToken cancellationToken)
    {
        var client = this.Client;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(this.invokeTimeout);
        try
        {
            await call(client).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CodingInvokeException.Unreachable((int)this.invokeTimeout.TotalSeconds);
        }
        catch (Exception ex) when (CodingErrorWire.TryDecode(ex.Message, out var code, out var message))
        {
            throw CodingInvokeException.FromWire(code, message);
        }
    }
}
