using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public class CodingToolBaseTests
{
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
