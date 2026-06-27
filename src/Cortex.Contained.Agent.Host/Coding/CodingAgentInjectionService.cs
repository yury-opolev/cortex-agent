using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// Subscribes to <see cref="CodingAgentEventBus"/> and enqueues a synthetic
/// user-role <see cref="AgentMessage"/> on the affected channel each time a
/// terminal event (final result / permission ask / clarification / error) arrives.
/// </summary>
public sealed partial class CodingAgentInjectionService : IHostedService
{
    private readonly CodingAgentEventBus bus;
    private readonly CodingAgentSessionStore store;
    private readonly AgentMessageChannel queue;
    private readonly ILogger<CodingAgentInjectionService> logger;

    public CodingAgentInjectionService(
        CodingAgentEventBus bus,
        CodingAgentSessionStore store,
        AgentMessageChannel queue,
        ILogger<CodingAgentInjectionService> logger)
    {
        this.bus = bus;
        this.store = store;
        this.queue = queue;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.bus.FinalResult += this.OnFinalResult;
        this.bus.PermissionRequest += this.OnPermissionRequest;
        this.bus.Question += this.OnQuestion;
        this.bus.PlanApproval += this.OnPlanApproval;
        this.bus.Error += this.OnError;
        this.bus.Stalled += this.OnStalled;
        this.bus.LimitReached += this.OnLimitReached;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.bus.FinalResult -= this.OnFinalResult;
        this.bus.PermissionRequest -= this.OnPermissionRequest;
        this.bus.Question -= this.OnQuestion;
        this.bus.PlanApproval -= this.OnPlanApproval;
        this.bus.Error -= this.OnError;
        this.bus.Stalled -= this.OnStalled;
        this.bus.LimitReached -= this.OnLimitReached;
        return Task.CompletedTask;
    }

    private void OnFinalResult(CodingFinalResultEvent evt)
    {
        var record = this.store.GetById(evt.SessionId);
        if (record is null)
        {
            this.LogUnknownSession(evt.SessionId);
            return;
        }

        var summary = TruncateForStorage(evt.FinalText, 500);
        var toolCallsJson = CodingAgentSessionStore.SerializeToolCalls(evt.ToolCalls);
        this.store.Upsert(record with
        {
            State = CodingSessionState.Idle,
            LastActivityAt = DateTimeOffset.UtcNow,
            LastAssistantSummary = summary,
            LastToolCallsJson = toolCallsJson,
        });

        this.Enqueue(record.ChannelId, CodingAgentEnvelope.BuildFinalResult(evt));
    }

    private void OnPermissionRequest(CodingPermissionRequestEvent evt)
    {
        var record = this.store.GetById(evt.SessionId);
        if (record is null)
        {
            this.LogUnknownSession(evt.SessionId);
            return;
        }

        this.store.Upsert(record with
        {
            State = CodingSessionState.AwaitingPermission,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.Enqueue(record.ChannelId, CodingAgentEnvelope.BuildPermissionRequest(evt));
    }

    private void OnQuestion(CodingQuestionRequestEvent evt)
    {
        var record = this.store.GetById(evt.SessionId);
        if (record is null)
        {
            this.LogUnknownSession(evt.SessionId);
            return;
        }

        this.store.Upsert(record with
        {
            State = CodingSessionState.AwaitingQuestion,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.Enqueue(record.ChannelId, CodingAgentEnvelope.BuildQuestion(evt));
    }

    private void OnPlanApproval(CodingPlanApprovalRequestEvent evt)
    {
        var record = this.store.GetById(evt.SessionId);
        if (record is null)
        {
            this.LogUnknownSession(evt.SessionId);
            return;
        }

        this.store.Upsert(record with
        {
            State = CodingSessionState.AwaitingPlan,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.Enqueue(record.ChannelId, CodingAgentEnvelope.BuildPlanApproval(evt));
    }

    private void OnError(CodingErrorEvent evt)
    {
        var record = this.store.GetById(evt.SessionId);
        if (record is null)
        {
            this.LogUnknownSession(evt.SessionId);
            return;
        }

        this.store.Upsert(record with
        {
            State = CodingSessionState.Crashed,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.Enqueue(record.ChannelId, CodingAgentEnvelope.BuildError(evt));
    }

    private void OnStalled(CodingStalledEvent evt)
    {
        var record = this.store.GetById(evt.SessionId);
        if (record is null)
        {
            this.LogUnknownSession(evt.SessionId);
            return;
        }

        this.store.Upsert(record with
        {
            State = CodingSessionState.Crashed,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.Enqueue(record.ChannelId, CodingAgentEnvelope.BuildStalled(evt));
    }

    private void OnLimitReached(CodingLimitReachedEvent evt)
    {
        var record = this.store.GetById(evt.SessionId);
        if (record is null)
        {
            this.LogUnknownSession(evt.SessionId);
            return;
        }

        // Recoverable, not a crash. The trailing turnComplete (OnFinalResult) owns the state transition
        // back to Idle and the final summary — we deliberately do NOT rewrite the record here, to avoid
        // clobbering that freshly-set summary in the concurrent turn-end window. We only surface the advisory.
        this.Enqueue(record.ChannelId, CodingAgentEnvelope.BuildLimitReached(evt));
    }

    private void Enqueue(string channelId, string envelope)
    {
        var message = new AgentMessage
        {
            ConversationId = channelId,
            ChannelId = channelId,
            Text = envelope,
            Source = AgentMessageSource.CodingAgentInjection,
        };
        if (!this.queue.TryEnqueue(message))
        {
            this.LogQueueFull(channelId);
        }
    }

    private static string? TruncateForStorage(string? input, int max)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return input.Length <= max ? input : input[..max] + "…";
    }

    [LoggerMessage(EventId = 9120, Level = LogLevel.Warning, Message = "External agent event for unknown session {sessionId}")]
    private partial void LogUnknownSession(string sessionId);

    [LoggerMessage(EventId = 9121, Level = LogLevel.Warning, Message = "External agent injection queue full for {channelId}")]
    private partial void LogQueueFull(string channelId);
}
