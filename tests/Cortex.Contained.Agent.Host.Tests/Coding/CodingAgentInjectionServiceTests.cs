using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public class CodingAgentInjectionServiceTests : IDisposable
{
    private readonly string tempRoot;
    private readonly CodingAgentSessionStore store;
    private readonly CodingAgentEventBus bus;
    private readonly AgentMessageChannel queue;
    private readonly CodingAgentInjectionService service;

    public CodingAgentInjectionServiceTests()
    {
        this.tempRoot = Path.Combine(Path.GetTempPath(), $"injection-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempRoot);
        this.store = new CodingAgentSessionStore(this.tempRoot);
        this.bus = new CodingAgentEventBus();
        this.queue = new AgentMessageChannel();
        this.service = new CodingAgentInjectionService(
            this.bus,
            this.store,
            this.queue,
            NullLogger<CodingAgentInjectionService>.Instance);

        this.service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        this.service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        this.store.Dispose();
        try
        {
            Directory.Delete(this.tempRoot, recursive: true);
        }
        catch
        {
            // ignore
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void FinalResult_KnownSession_EnqueuesEnvelope()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaiseFinalResult(new CodingFinalResultEvent
        {
            SessionId = sessionId,
            TaskId = "t1",
            FinalText = "Done.",
        });

        Assert.True(this.queue.TryRead(out var message));
        Assert.NotNull(message);
        Assert.Equal("ch-1", message!.ChannelId);
        Assert.Equal(AgentMessageSource.CodingAgentInjection, message.Source);
        Assert.Contains("Done.", message.Text);
    }

    [Fact]
    public void PermissionRequest_TransitionsStateAndEnqueues()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaisePermissionRequest(new CodingPermissionRequestEvent
        {
            SessionId = sessionId,
            RequestId = "r1",
            ToolName = "Bash",
            InputPreview = "{}",
        });

        var record = this.store.GetById(sessionId);
        Assert.Equal(CodingSessionState.AwaitingPermission, record!.State);
        Assert.True(this.queue.TryRead(out var message));
        Assert.Contains("status=awaiting-permission", message!.Text);
    }

    [Fact]
    public void Error_TransitionsToCrashed()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaiseError(new CodingErrorEvent
        {
            SessionId = sessionId,
            Message = "boom",
        });

        var record = this.store.GetById(sessionId);
        Assert.Equal(CodingSessionState.Crashed, record!.State);
        Assert.True(this.queue.TryRead(out _));
    }

    [Fact]
    public void Stalled_TransitionsToCrashed_AndEnqueuesStalledEnvelope()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaiseStalled(new CodingStalledEvent
        {
            SessionId = sessionId,
            IdleSeconds = 305,
            WasStreaming = false,
            Message = "coda appears stalled — no activity for 305s; terminating session.",
        });

        var record = this.store.GetById(sessionId);
        Assert.Equal(CodingSessionState.Crashed, record!.State);
        Assert.True(this.queue.TryRead(out var message));
        Assert.Contains("status=stalled", message!.Text);
    }

    [Fact]
    public void LimitReached_StaysRecoverable_AndEnqueuesLimitEnvelope()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaiseLimitReached(new CodingLimitReachedEvent
        {
            SessionId = sessionId,
            Kind = "max_tokens",
            Message = "The response was truncated (max_tokens reached).",
        });

        var record = this.store.GetById(sessionId);
        // Recoverable: OnLimitReached must NOT crash the session (the trailing turnComplete owns the
        // state transition back to Idle); it only surfaces the advisory.
        Assert.NotEqual(CodingSessionState.Crashed, record!.State);
        Assert.True(this.queue.TryRead(out var message));
        Assert.Contains("status=limit-reached", message!.Text);
    }

    [Fact]
    public void UnknownSession_DoesNotEnqueue()
    {
        this.bus.RaiseFinalResult(new CodingFinalResultEvent
        {
            SessionId = "non-existent",
            TaskId = "t1",
            FinalText = "?",
        });

        Assert.False(this.queue.TryRead(out _));
    }

    [Fact]
    public void PermissionRequest_PersistsPendingRequest_SoStatusCanNameIt()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaisePermissionRequest(new CodingPermissionRequestEvent
        {
            SessionId = sessionId,
            RequestId = "r-persist",
            ToolName = "Bash",
            InputPreview = "git push",
        });

        var pending = CodingAgentSessionStore.DeserializePendingRequest(
            this.store.GetById(sessionId)!.PendingRequestJson);

        Assert.NotNull(pending);
        Assert.Equal("r-persist", pending!.RequestId);
        Assert.Equal(PendingCodingRequestKind.Permission, pending.Kind);
        Assert.Equal("Bash", pending.ToolName);
    }

    [Fact]
    public void Question_PersistsPendingRequest_WithQuestionAndOptions()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaiseQuestion(new CodingQuestionRequestEvent
        {
            SessionId = sessionId,
            RequestId = "q-1",
            Question = "Which approach?",
            Options = ["A", "B"],
            MultiSelect = false,
        });

        var pending = CodingAgentSessionStore.DeserializePendingRequest(
            this.store.GetById(sessionId)!.PendingRequestJson);

        Assert.NotNull(pending);
        Assert.Equal(PendingCodingRequestKind.Question, pending!.Kind);
        Assert.Equal("Which approach?", pending.Question);
        Assert.Equal(["A", "B"], pending.Options);
    }

    [Fact]
    public void PlanApproval_PersistsPendingRequest_WithPlanText()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaisePlanApproval(new CodingPlanApprovalRequestEvent
        {
            SessionId = sessionId,
            RequestId = "p-1",
            Plan = "Step 1: do X",
        });

        var pending = CodingAgentSessionStore.DeserializePendingRequest(
            this.store.GetById(sessionId)!.PendingRequestJson);

        Assert.NotNull(pending);
        Assert.Equal(PendingCodingRequestKind.Plan, pending!.Kind);
        Assert.Equal("Step 1: do X", pending.Plan);
    }

    [Fact]
    public void FinalResult_ClearsPendingRequest()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaisePermissionRequest(new CodingPermissionRequestEvent
        {
            SessionId = sessionId,
            RequestId = "r-cleared",
            ToolName = "Bash",
            InputPreview = "git push",
        });

        this.bus.RaiseFinalResult(new CodingFinalResultEvent
        {
            SessionId = sessionId,
            TaskId = "t1",
            FinalText = "Pushed.",
        });

        Assert.Null(this.store.GetById(sessionId)!.PendingRequestJson);
    }

    [Fact]
    public void PromptExpired_ClearsPendingRequestAndWarnsTheAgent()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaisePermissionRequest(new CodingPermissionRequestEvent
        {
            SessionId = sessionId,
            RequestId = "r-expired",
            ToolName = "Bash",
            InputPreview = "git push",
        });
        Assert.True(this.queue.TryRead(out _));

        this.bus.RaisePromptExpired(new CodingPromptExpiredEvent
        {
            SessionId = sessionId,
            RequestId = "r-expired",
            Kind = PendingCodingRequestKind.Permission,
            Resolution = CodingPromptResolution.Expired,
            Message = "permission for Bash was auto-denied after 900s with no response",
        });

        // Leaving it stored would keep the offline fallback advertising a dead requestId.
        var stored = this.store.GetById(sessionId)!;
        Assert.Null(stored.PendingRequestJson);

        // …and an Awaiting* state with nothing to name is the dead end this fix removes.
        Assert.Equal(CodingSessionState.Working, stored.State);

        Assert.True(this.queue.TryRead(out var message));
        Assert.Contains("status=prompt-expired", message!.Text);
        Assert.Contains("r-expired", message.Text);
        Assert.Contains("offer to send the instruction again", message.Text);
    }

    [Fact]
    public void PromptExpired_Abandoned_DoesNotTellTheAgentToResend()
    {
        // The session is gone — "try again" would point at a session that no longer exists.
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaisePromptExpired(new CodingPromptExpiredEvent
        {
            SessionId = sessionId,
            RequestId = "r-abandoned",
            Kind = PendingCodingRequestKind.Permission,
            Resolution = CodingPromptResolution.Abandoned,
            Message = "permission for Bash was abandoned unanswered: coda exited",
        });

        Assert.True(this.queue.TryRead(out var message));
        Assert.Contains("do NOT resend", message!.Text);
    }

    [Fact]
    public void PromptExpired_AfterACrash_LeavesTheTerminalStateAlone()
    {
        var sessionId = Guid.NewGuid().ToString();
        this.store.Upsert(MakeRecord(sessionId, "ch-1"));

        this.bus.RaiseError(new CodingErrorEvent { SessionId = sessionId, Message = "coda exited" });
        this.bus.RaisePromptExpired(new CodingPromptExpiredEvent
        {
            SessionId = sessionId,
            RequestId = "r-abandoned",
            Kind = PendingCodingRequestKind.Permission,
            Resolution = CodingPromptResolution.Abandoned,
            Message = "permission for Bash was abandoned unanswered: coda exited",
        });

        // Un-crashing the record would be worse than the bug being fixed.
        Assert.Equal(CodingSessionState.Crashed, this.store.GetById(sessionId)!.State);
    }

    private static CodingAgentSessionRecord MakeRecord(string id, string channel)
    {
        var now = DateTimeOffset.UtcNow;
        return new CodingAgentSessionRecord
        {
            SessionId = id,
            ChannelId = channel,
            WorkingFolder = "C:\\repo",
            Policy = CodingPolicy.Prompt,
            State = CodingSessionState.Working,
            CreatedAt = now,
            LastActivityAt = now,
        };
    }
}
