using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public class CodingAgentEnvelopeTests
{
    [Fact]
    public void BuildFinalResult_IncludesSessionAndTaskHeaders()
    {
        var evt = new CodingFinalResultEvent
        {
            SessionId = "s1",
            TaskId = "t1",
            FinalText = "Added retries.",
            ToolCalls = new[]
            {
                new CodingToolCall { Name = "Edit", ArgsSummary = "Embed.cs", Status = "completed", TimestampUtc = DateTimeOffset.UtcNow },
            },
        };

        var envelope = CodingAgentEnvelope.BuildFinalResult(evt);

        Assert.Contains("[coding session=s1 status=ready taskId=t1]", envelope);
        Assert.Contains("Final:", envelope);
        Assert.Contains("Added retries.", envelope);
        Assert.Contains("Edit", envelope);
        Assert.Contains("Embed.cs", envelope);
    }

    [Fact]
    public void BuildPermissionRequest_ContainsAllowDenyHints()
    {
        var evt = new CodingPermissionRequestEvent
        {
            SessionId = "s1",
            RequestId = "t1",
            ToolName = "Bash",
            InputPreview = "{\"command\":\"rm *.bak\"}",
        };

        var envelope = CodingAgentEnvelope.BuildPermissionRequest(evt);

        Assert.Contains("status=awaiting-permission", envelope);
        Assert.Contains("Bash", envelope);
        Assert.Contains("rm *.bak", envelope);
        Assert.Contains("allow_once", envelope);
        Assert.Contains("allow_always", envelope);
        Assert.Contains("deny", envelope);
    }

    [Fact]
    public void BuildPermission_uses_requestId_and_inputPreview()
    {
        var evt = new CodingPermissionRequestEvent
        {
            SessionId = "s1",
            RequestId = "r3",
            ToolName = "run_command",
            InputPreview = "dotnet ef database drop",
        };

        var envelope = CodingAgentEnvelope.BuildPermissionRequest(evt);

        Assert.Contains("status=awaiting-permission requestId=r3", envelope);
        Assert.Contains("run_command", envelope);
        Assert.Contains("dotnet ef database drop", envelope);
    }

    [Fact]
    public void BuildQuestion_ContainsQuestionAndCallbackHint()
    {
        var evt = new CodingQuestionRequestEvent
        {
            SessionId = "s1",
            RequestId = "r1",
            Question = "Should I overwrite the existing file?",
        };

        var envelope = CodingAgentEnvelope.BuildQuestion(evt);

        Assert.Contains("status=awaiting-question", envelope);
        Assert.Contains("Should I overwrite the existing file?", envelope);
        Assert.Contains("coding_session_respond", envelope);
    }

    [Fact]
    public void BuildQuestion_renders_options_and_requestId()
    {
        var evt = new CodingQuestionRequestEvent
        {
            SessionId = "s1",
            RequestId = "r9",
            Question = "Which database?",
            Options = ["Postgres", "SQLite"],
            MultiSelect = false,
        };

        var envelope = CodingAgentEnvelope.BuildQuestion(evt);

        Assert.Contains("[coding session=s1 status=awaiting-question requestId=r9]", envelope);
        Assert.Contains("Question: Which database?", envelope);
        Assert.Contains("Options: Postgres | SQLite", envelope);
        Assert.Contains("coding_session_respond", envelope);
    }

    [Fact]
    public void BuildPlanApproval_renders_plan_and_requestId()
    {
        var evt = new CodingPlanApprovalRequestEvent
        {
            SessionId = "s1",
            RequestId = "r5",
            Plan = "1. Add retries\n2. Add tests",
        };

        var envelope = CodingAgentEnvelope.BuildPlanApproval(evt);

        Assert.Contains("[coding session=s1 status=awaiting-plan requestId=r5]", envelope);
        Assert.Contains("Plan:", envelope);
        Assert.Contains("Add retries", envelope);
        Assert.Contains("approve", envelope);
    }

    [Fact]
    public void BuildError_IncludesExitCodeAndStderr()
    {
        var evt = new CodingErrorEvent
        {
            SessionId = "s1",
            ExitCode = 137,
            StderrTail = "Killed",
            Message = "claude exited unexpectedly",
        };

        var envelope = CodingAgentEnvelope.BuildError(evt);

        Assert.Contains("status=crashed", envelope);
        Assert.Contains("exitCode=137", envelope);
        Assert.Contains("Killed", envelope);
        Assert.Contains("claude exited unexpectedly", envelope);
    }

    [Fact]
    public void BuildStalled_RendersStalledStatusIdleAndResumeHint()
    {
        var evt = new CodingStalledEvent
        {
            SessionId = "s7",
            IdleSeconds = 312,
            WasStreaming = true,
            StreamedChars = 1024,
            StderrTail = "coda: awaiting model response",
            Message = "coda appears stalled — no activity for 312s; terminating session.",
        };

        var envelope = CodingAgentEnvelope.BuildStalled(evt);

        Assert.Contains("[coding session=s7 status=stalled idleSeconds=312]", envelope);
        Assert.Contains("coda appears stalled", envelope);
        Assert.Contains("awaiting model response", envelope);
        Assert.Contains("coding_session_resume", envelope);
        // Anti-churn: must explicitly steer away from blindly resending the same instruction.
        Assert.Contains("resend", envelope, StringComparison.OrdinalIgnoreCase);
    }
}
