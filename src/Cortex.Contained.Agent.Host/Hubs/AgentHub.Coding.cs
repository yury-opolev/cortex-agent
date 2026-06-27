using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>
/// External-agent push-back methods on the SignalR hub. Bridge invokes these
/// when a Claude Code session emits a terminal event; the agent forwards them to
/// the injected <c>CodingAgentEventBus</c> for injection-service consumption.
/// </summary>
public sealed partial class AgentHub
{
    public Task NotifyCodingFinalResult(CodingFinalResultEvent evt)
    {
        this.externalAgentBus.RaiseFinalResult(evt);
        return Task.CompletedTask;
    }

    public Task NotifyCodingPermissionRequest(CodingPermissionRequestEvent evt)
    {
        this.externalAgentBus.RaisePermissionRequest(evt);
        return Task.CompletedTask;
    }

    public Task NotifyCodingQuestion(CodingQuestionRequestEvent evt)
    {
        this.externalAgentBus.RaiseQuestion(evt);
        return Task.CompletedTask;
    }

    public Task NotifyCodingPlanApproval(CodingPlanApprovalRequestEvent evt)
    {
        this.externalAgentBus.RaisePlanApproval(evt);
        return Task.CompletedTask;
    }

    public Task NotifyCodingError(CodingErrorEvent evt)
    {
        this.externalAgentBus.RaiseError(evt);
        return Task.CompletedTask;
    }

    public Task NotifyCodingStalled(CodingStalledEvent evt)
    {
        this.externalAgentBus.RaiseStalled(evt);
        return Task.CompletedTask;
    }

    public Task NotifyCodingLimitReached(CodingLimitReachedEvent evt)
    {
        this.externalAgentBus.RaiseLimitReached(evt);
        return Task.CompletedTask;
    }
}
