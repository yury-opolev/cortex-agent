using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// Process-wide multiplexer for the four push-back events that arrive from the Bridge
/// via the SignalR hub. The hub raises events here; the injection service subscribes.
/// </summary>
public sealed class CodingAgentEventBus
{
    public event Action<CodingFinalResultEvent>? FinalResult;
    public event Action<CodingPermissionRequestEvent>? PermissionRequest;
    public event Action<CodingQuestionRequestEvent>? Question;
    public event Action<CodingPlanApprovalRequestEvent>? PlanApproval;
    public event Action<CodingErrorEvent>? Error;
    public event Action<CodingStalledEvent>? Stalled;
    public event Action<CodingLimitReachedEvent>? LimitReached;

    public void RaiseFinalResult(CodingFinalResultEvent evt) => this.FinalResult?.Invoke(evt);

    public void RaisePermissionRequest(CodingPermissionRequestEvent evt) => this.PermissionRequest?.Invoke(evt);

    public void RaiseQuestion(CodingQuestionRequestEvent evt) => this.Question?.Invoke(evt);

    public void RaisePlanApproval(CodingPlanApprovalRequestEvent evt) => this.PlanApproval?.Invoke(evt);

    public void RaiseError(CodingErrorEvent evt) => this.Error?.Invoke(evt);

    public void RaiseStalled(CodingStalledEvent evt) => this.Stalled?.Invoke(evt);

    public void RaiseLimitReached(CodingLimitReachedEvent evt) => this.LimitReached?.Invoke(evt);
}
