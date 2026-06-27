using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public class CodingAgentSessionToolsTests : IDisposable
{
    private readonly string tempRoot;
    private readonly CodingAgentSessionStore store;
    private readonly ICodingAgent agent;

    public CodingAgentSessionToolsTests()
    {
        this.tempRoot = Path.Combine(Path.GetTempPath(), $"tools-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempRoot);
        this.store = new CodingAgentSessionStore(this.tempRoot);
        this.agent = Substitute.For<ICodingAgent>();
    }

    public void Dispose()
    {
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

    private static ToolExecutionContext Ctx(string channelId = "ch-1") => new()
    {
        ConversationId = channelId,
        ChannelId = channelId,
    };

    [Fact]
    public async Task SessionStart_MissingFolder_Errors()
    {
        var tool = new CodingSessionStartTool(this.agent, this.store);
        var result = await tool.ExecuteAsync("{}", Ctx(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("workingFolder", result.Error!);
    }

    [Fact]
    public async Task SessionStart_AgentThrowsStartFailed_ReturnsDefinitiveFailure()
    {
        var tool = new CodingSessionStartTool(this.agent, this.store);
        this.agent.StartSessionAsync(Arg.Any<CodingStartRequest>(), Arg.Any<CancellationToken>())
            .Returns<CodingStatus>(_ => throw CodingInvokeException.StartFailed("400 model_not_supported", stderrTail: null));

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { workingFolder = "C:\\repo" }),
            Ctx(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("coda_start_failed", result.Content);
        Assert.Contains("No session is running", result.Error!);
        Assert.Contains("400 model_not_supported", result.Error!);
        Assert.Null(this.store.GetById("any"));
    }

    [Fact]
    public async Task SessionStart_ChannelHasActive_StartsAdditionalSession()
    {
        var tool = new CodingSessionStartTool(this.agent, this.store);
        this.store.Upsert(new CodingAgentSessionRecord
        {
            SessionId = "existing",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\foo",
            Policy = CodingPolicy.Prompt,
            State = CodingSessionState.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.agent.StartSessionAsync(Arg.Any<CodingStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CodingStatus
            {
                SessionId = "second",
                ChannelId = "ch-1",
                WorkingFolder = "C:\\bar",
                State = CodingSessionState.Idle,
                Policy = CodingPolicy.Prompt,
            });

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { workingFolder = "C:\\bar" }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("second", result.Content);
        Assert.NotNull(this.store.GetById("existing"));
        Assert.NotNull(this.store.GetById("second"));
    }

    [Fact]
    public async Task SessionStart_HappyPath_CallsAgentAndStores()
    {
        var tool = new CodingSessionStartTool(this.agent, this.store);

        this.agent.StartSessionAsync(Arg.Any<CodingStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CodingStatus
            {
                SessionId = "new-id",
                ChannelId = "ch-1",
                WorkingFolder = "C:\\repo",
                State = CodingSessionState.Idle,
                Policy = CodingPolicy.Prompt,
            });

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { workingFolder = "C:\\repo" }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("new-id", result.Content);
        var record = this.store.GetById("new-id");
        Assert.NotNull(record);
    }

    [Fact]
    public async Task SessionStart_GoalAndSessionMemory_ForwardedToRequest()
    {
        var tool = new CodingSessionStartTool(this.agent, this.store);

        CodingStartRequest? captured = null;
        this.agent.StartSessionAsync(Arg.Do<CodingStartRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new CodingStatus
            {
                SessionId = "new-id",
                ChannelId = "ch-1",
                WorkingFolder = "C:\\repo",
                State = CodingSessionState.Idle,
                Policy = CodingPolicy.Prompt,
            });

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { workingFolder = "C:\\repo", goal = "all tests pass", sessionMemory = true }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Equal("all tests pass", captured.Goal);
        Assert.True(captured.SessionMemory);
    }

    [Fact]
    public async Task SessionSend_MultipleActiveSessions_NoSessionId_ReturnsAmbiguousError()
    {
        var tool = new CodingSessionSendTool(this.agent, this.store);
        foreach (var id in new[] { "s1", "s2" })
        {
            this.store.Upsert(new CodingAgentSessionRecord
            {
                SessionId = id,
                ChannelId = "ch-1",
                WorkingFolder = "C:\\foo",
                Policy = CodingPolicy.Prompt,
                State = CodingSessionState.Idle,
                CreatedAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow,
            });
        }

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { message = "hi" }),
            Ctx(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(CodingBridgeErrorCodes.AmbiguousSession, result.Content);
        await this.agent.DidNotReceive().SendMessageAsync(Arg.Any<CodingSendRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionSend_NoActiveSession_Errors()
    {
        var tool = new CodingSessionSendTool(this.agent, this.store);
        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { message = "hi" }),
            Ctx(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(CodingBridgeErrorCodes.NoActiveSession, result.Content);
    }

    [Fact]
    public async Task SessionSend_DefaultsToActiveChannelSession()
    {
        var tool = new CodingSessionSendTool(this.agent, this.store);
        this.store.Upsert(new CodingAgentSessionRecord
        {
            SessionId = "active-id",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            Policy = CodingPolicy.Prompt,
            State = CodingSessionState.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.agent.SendMessageAsync(Arg.Any<CodingSendRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CodingSendResponse
            {
                TaskId = "t1",
                SessionId = "active-id",
                State = CodingSessionState.Working,
            });

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { message = "do something" }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        await this.agent.Received().SendMessageAsync(
            Arg.Is<CodingSendRequest>(r => r.SessionId == "active-id" && r.Message == "do something"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionSetGoal_WithGoalAndBudget_CallsAgentWithParsedArgs()
    {
        var tool = new CodingSessionSetGoalTool(this.agent, this.store);
        this.store.Upsert(new CodingAgentSessionRecord
        {
            SessionId = "active-id",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            Policy = CodingPolicy.Yolo,
            State = CodingSessionState.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.agent.SetGoalAsync(Arg.Any<CodingSetGoalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CodingSetGoalResponse { SessionId = "active-id", Goal = "all tests pass", MaxDuration = "30m", MaxContinuations = 200 });

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { goal = "all tests pass", maxDuration = "30m", maxContinuations = 200 }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("all tests pass", result.Content);
        await this.agent.Received().SetGoalAsync(
            Arg.Is<CodingSetGoalRequest>(r => r.SessionId == "active-id" && r.Goal == "all tests pass" && r.MaxDuration == "30m" && r.MaxContinuations == 200),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionSetGoal_EmptyGoal_ClearsGoal()
    {
        var tool = new CodingSessionSetGoalTool(this.agent, this.store);
        this.store.Upsert(new CodingAgentSessionRecord
        {
            SessionId = "active-id",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            Policy = CodingPolicy.Yolo,
            State = CodingSessionState.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.agent.SetGoalAsync(Arg.Any<CodingSetGoalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CodingSetGoalResponse { SessionId = "active-id", Goal = null });

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { goal = "   " }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"cleared\":true", result.Content);
        await this.agent.Received().SetGoalAsync(
            Arg.Is<CodingSetGoalRequest>(r => r.SessionId == "active-id" && r.Goal == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionEnd_DefaultsToActiveAndMarksEnded()
    {
        var tool = new CodingSessionEndTool(this.agent, this.store);
        this.store.Upsert(new CodingAgentSessionRecord
        {
            SessionId = "active-id",
            ChannelId = "ch-1",
            WorkingFolder = "C:\\repo",
            Policy = CodingPolicy.Prompt,
            State = CodingSessionState.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        });

        this.agent.EndSessionAsync("active-id", Arg.Any<CancellationToken>())
            .Returns(new CodingEndResponse
            {
                SessionId = "active-id",
                State = CodingSessionState.Ended,
            });

        var result = await tool.ExecuteAsync("{}", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(this.store.ListActiveByChannel("ch-1"));
    }

    [Fact]
    public async Task SessionList_DelegatesToAgent()
    {
        var tool = new CodingSessionListTool(this.agent);
        this.agent.ListSessionsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CodingStatus
                {
                    SessionId = "a",
                    ChannelId = "ch-1",
                    WorkingFolder = "C:\\a",
                    State = CodingSessionState.Idle,
                    Policy = CodingPolicy.Prompt,
                },
            });

        var result = await tool.ExecuteAsync("{}", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"a\"", result.Content);
    }

    [Fact]
    public async Task SessionHistory_ExplicitSessionId_ReturnsSerializedMessages()
    {
        var tool = new CodingSessionHistoryTool(this.agent, this.store);
        this.agent.GetHistoryAsync("sess-1", null, Arg.Any<CancellationToken>())
            .Returns(new CodingHistory
            {
                Messages =
                [
                    new CodingHistoryMessage { Role = "user", Content = "hello" },
                    new CodingHistoryMessage { Role = "assistant", Content = "hi there" },
                ],
                NextIndex = null,
            });

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { sessionId = "sess-1" }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hello", result.Content);
        Assert.Contains("hi there", result.Content);
        Assert.Contains("assistant", result.Content);
        await this.agent.Received().GetHistoryAsync("sess-1", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionHistory_WithSinceIndex_RequestsIncrementalAndReturnsNextIndex()
    {
        var tool = new CodingSessionHistoryTool(this.agent, this.store);
        this.agent.GetHistoryAsync("sess-1", 2, Arg.Any<CancellationToken>())
            .Returns(new CodingHistory
            {
                Messages =
                [
                    new CodingHistoryMessage { Role = "user", Content = "m2" },
                ],
                NextIndex = 3,
            });

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { sessionId = "sess-1", sinceIndex = 2 }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("m2", result.Content);
        Assert.Contains("nextIndex", result.Content);
        Assert.Contains("3", result.Content);
        await this.agent.Received().GetHistoryAsync("sess-1", 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionHistory_AgentThrowsInvokeException_ReturnsDefinitiveFailure()
    {
        var tool = new CodingSessionHistoryTool(this.agent, this.store);
        this.agent.GetHistoryAsync("sess-1", null, Arg.Any<CancellationToken>())
            .Returns<CodingHistory>(_ => throw CodingInvokeException.FromWire(
                CodingBridgeErrorCodes.NoActiveSession, "No live session with id sess-1."));

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { sessionId = "sess-1" }),
            Ctx(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(CodingBridgeErrorCodes.NoActiveSession, result.Content);
        Assert.Contains("No live session", result.Error!);
    }

    [Fact]
    public async Task SessionRespond_RequiresRequestIdAndResponse()
    {
        var tool = new CodingSessionRespondTool(this.agent);
        var result = await tool.ExecuteAsync("{}", Ctx(), CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task SessionRespond_DelegatesToAgent()
    {
        var tool = new CodingSessionRespondTool(this.agent);
        var args = JsonSerializer.Serialize(new { requestId = "r1", response = "allow_once" });
        var result = await tool.ExecuteAsync(args, Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        await this.agent.Received().RespondAsync(
            Arg.Is<CodingRespondRequest>(r => r.RequestId == "r1" && r.Response == "allow_once"),
            Arg.Any<CancellationToken>());
    }
}
