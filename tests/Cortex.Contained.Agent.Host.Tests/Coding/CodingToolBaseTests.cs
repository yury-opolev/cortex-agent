using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public class CodingToolBaseTests
{
    [Fact]
    public void SnapshotPayload_IncludesPendingRequest_SoRespondCanBeCalled()
    {
        var status = new CodingStatus
        {
            SessionId = "s1",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            State = CodingSessionState.AwaitingPermission,
            Policy = CodingPolicy.YoloSafe,
            PendingRequest = new PendingCodingRequest
            {
                RequestId = "req-42",
                Kind = PendingCodingRequestKind.Permission,
                ToolName = "Bash",
                InputPreview = "git push origin main",
                RequestedAt = DateTimeOffset.UtcNow,
            },
        };

        var payload = CodingToolBase.SnapshotPayload(status);
        var json = JsonSerializer.Serialize(payload, CodingToolBase.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var pending = doc.RootElement.GetProperty("pendingRequest");

        Assert.Equal("req-42", pending.GetProperty("requestId").GetString());
        Assert.Equal("Permission", pending.GetProperty("kind").GetString());
        Assert.Equal("Bash", pending.GetProperty("toolName").GetString());
        Assert.Equal("git push origin main", pending.GetProperty("inputPreview").GetString());
    }

    [Fact]
    public void SnapshotPayload_PendingRequestIsNull_WhenNothingIsAwaited()
    {
        var status = new CodingStatus
        {
            SessionId = "s1",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            State = CodingSessionState.Idle,
            Policy = CodingPolicy.Prompt,
        };

        var payload = CodingToolBase.SnapshotPayload(status);
        var json = JsonSerializer.Serialize(payload, CodingToolBase.JsonOptions);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("pendingRequest").ValueKind);
    }

    [Fact]
    public void ToRecord_CarriesPendingRequest_SoItSurvivesAnAgentHostRestart()
    {
        var status = new CodingStatus
        {
            SessionId = "s1",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            State = CodingSessionState.AwaitingQuestion,
            Policy = CodingPolicy.Prompt,
            PendingRequest = new PendingCodingRequest
            {
                RequestId = "req-7",
                Kind = PendingCodingRequestKind.Question,
                Question = "Which approach?",
                Options = ["A", "B"],
                RequestedAt = DateTimeOffset.UtcNow,
            },
        };

        var record = CodingToolBase.ToRecord(status);
        var restored = CodingAgentSessionStore.DeserializePendingRequest(record.PendingRequestJson);

        Assert.NotNull(restored);
        Assert.Equal("req-7", restored!.RequestId);
        Assert.Equal(PendingCodingRequestKind.Question, restored.Kind);
        Assert.Equal("Which approach?", restored.Question);
        Assert.Equal(["A", "B"], restored.Options);
    }

    [Fact]
    public void SnapshotPayload_IncludesLastPromptExpiry_SoAnAutoRefusalIsExplained()
    {
        var status = new CodingStatus
        {
            SessionId = "s1",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            State = CodingSessionState.Idle,
            Policy = CodingPolicy.Prompt,
            LastPromptExpiry = "permission for Bash was auto-denied after 900s with no response",
        };

        var payload = CodingToolBase.SnapshotPayload(status);
        var json = JsonSerializer.Serialize(payload, CodingToolBase.JsonOptions);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(
            "permission for Bash was auto-denied after 900s with no response",
            doc.RootElement.GetProperty("lastPromptExpiry").GetString());
    }

    [Fact]
    public void FromWire_UnknownRequest_DoesNotReportTheSessionTerminated()
    {
        // "No such prompt" says nothing about the session — it is alive and still answerable
        // once the real requestId is read back from coding_session_status.
        var ex = CodingInvokeException.FromWire(CodingBridgeErrorCodes.UnknownRequest, "no such prompt");

        Assert.Equal("unknown_request", ex.Code);
        Assert.False(ex.SessionTerminated);
    }

    [Fact]
    public void FromException_UnknownRequest_SurfacesTheStableCodeAsAFailure()
    {
        var result = CodingToolBase.FromException(
            CodingInvokeException.FromWire(CodingBridgeErrorCodes.UnknownRequest, "no such prompt"));

        Assert.False(result.Success);
        Assert.Contains("unknown_request", result.Content);
    }

    [Fact]
    public void FromException_CodingInvokeException_PreservesStableCode()
    {
        var result = CodingToolBase.FromException(CodingInvokeException.Unreachable(45));

        Assert.False(result.Success);
        Assert.Contains("coda_unreachable", result.Content);
        Assert.Contains("state is unknown", result.Error!);
    }

    [Fact]
    public void FromException_OtherException_IsInternalError()
    {
        var result = CodingToolBase.FromException(new InvalidOperationException("boom"));

        Assert.False(result.Success);
        Assert.Contains("internal_error", result.Content);
        Assert.Contains("boom", result.Error!);
    }


    [Fact]
    public void SnapshotPayload_IncludesTelemetryUsageAndLastError()
    {
        var status = new CodingStatus
        {
            SessionId = "s1",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            State = CodingSessionState.Idle,
            Policy = CodingPolicy.Prompt,
            TelemetryLogPath = "/tmp/coda/telemetry-abc.log",
            LastError = "coda blew up",
            InputTokens = 1234L,
            OutputTokens = 567L,
        };

        var payload = CodingToolBase.SnapshotPayload(status);
        var json = JsonSerializer.Serialize(payload, CodingToolBase.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("/tmp/coda/telemetry-abc.log", root.GetProperty("telemetryLogPath").GetString());
        Assert.Equal("coda blew up", root.GetProperty("lastError").GetString());
        Assert.Equal(1234L, root.GetProperty("inputTokens").GetInt64());
        Assert.Equal(567L, root.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public void SnapshotPayload_IncludesLiveStreamingFields()
    {
        var lastStream = DateTimeOffset.UtcNow;
        var status = new CodingStatus
        {
            SessionId = "s1",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            State = CodingSessionState.Working,
            Policy = CodingPolicy.Yolo,
            IsStreaming = true,
            StreamedChars = 2048L,
            StreamedChunks = 64L,
            LastStreamActivityAt = lastStream,
            CurrentActivity = "streaming LLM response (2048 chars, 64 chunks)",
        };

        var payload = CodingToolBase.SnapshotPayload(status);
        var json = JsonSerializer.Serialize(payload, CodingToolBase.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("isStreaming").GetBoolean());
        Assert.Equal(2048L, root.GetProperty("streamedChars").GetInt64());
        Assert.Equal(64L, root.GetProperty("streamedChunks").GetInt64());
        Assert.Equal("streaming LLM response (2048 chars, 64 chunks)", root.GetProperty("currentActivity").GetString());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("lastStreamActivityAt").ValueKind);
    }

    [Fact]
    public void SnapshotPayload_IncludesGoalStatus()
    {
        var status = new CodingStatus
        {
            SessionId = "s1",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            State = CodingSessionState.Idle,
            Policy = CodingPolicy.Yolo,
            GoalStatus = new CodingGoalStatus
            {
                Outcome = "Met",
                Remaining = null,
                Continuations = 5,
                ElapsedSeconds = 123.4,
                Escalated = false,
                ExtensionUsed = false,
            },
        };

        var payload = CodingToolBase.SnapshotPayload(status);
        var json = JsonSerializer.Serialize(payload, CodingToolBase.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var goal = doc.RootElement.GetProperty("goalStatus");

        Assert.Equal("Met", goal.GetProperty("outcome").GetString());
        Assert.Equal(5, goal.GetProperty("continuations").GetInt32());
        Assert.Equal(123.4, goal.GetProperty("elapsedSeconds").GetDouble());
    }

    [Fact]
    public void SnapshotPayload_NoGoal_GoalStatusIsNull()
    {
        var status = new CodingStatus
        {
            SessionId = "s1",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            State = CodingSessionState.Idle,
            Policy = CodingPolicy.Prompt,
        };

        var payload = CodingToolBase.SnapshotPayload(status);
        var json = JsonSerializer.Serialize(payload, CodingToolBase.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("goalStatus").ValueKind);
    }
}
