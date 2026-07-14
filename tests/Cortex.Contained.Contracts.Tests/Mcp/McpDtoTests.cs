using System.Text.Json;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Contracts.Tests.Mcp;

public class McpDtoTests
{
    [Fact]
    public void Ok_Result_SucceededOutcome_IsNotError_AndCarriesContent()
    {
        var result = McpToolResult.Ok("inv-1", "x");

        Assert.Equal("inv-1", result.InvocationId);
        Assert.Equal(McpToolOutcome.Succeeded, result.Outcome);
        Assert.Equal(McpFailureKind.None, result.FailureKind);
        Assert.False(result.IsError);
        Assert.Equal("x", result.Content);
        Assert.False(result.NeedsAuth);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Fail_Result_IsDefinitiveFailure_AndSurfacesNeedsAuth()
    {
        var result = McpToolResult.Fail("inv-1", McpFailureKind.Authentication, "e", needsAuth: true);

        Assert.Equal("inv-1", result.InvocationId);
        Assert.Equal(McpToolOutcome.Failed, result.Outcome);
        Assert.Equal(McpFailureKind.Authentication, result.FailureKind);
        Assert.True(result.IsError);
        Assert.Equal("e", result.Error);
        Assert.True(result.NeedsAuth);
        Assert.Equal(string.Empty, result.Content);
    }

    [Fact]
    public void Unknown_Result_IsError_ButNotDefinitiveFailed()
    {
        var result = McpToolResult.Unknown("inv-1", McpFailureKind.Timeout, "outcome unknown");

        Assert.Equal(McpToolOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(McpFailureKind.Timeout, result.FailureKind);
        Assert.True(result.IsError);
        Assert.NotEqual(McpToolOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void Cancelled_Result_CarriesCancellationFailureKind()
    {
        var result = McpToolResult.Cancelled("inv-1", "cancelled before dispatch");

        Assert.Equal(McpToolOutcome.Cancelled, result.Outcome);
        Assert.Equal(McpFailureKind.Cancellation, result.FailureKind);
        Assert.True(result.IsError);
    }

    [Fact]
    public void IsError_IsComputedFromOutcome_ForEveryOutcome()
    {
        foreach (var outcome in Enum.GetValues<McpToolOutcome>())
        {
            var result = new McpToolResult { InvocationId = "inv-1", Outcome = outcome };

            Assert.Equal(outcome != McpToolOutcome.Succeeded, result.IsError);
        }
    }

    [Fact]
    public void Catalog_Default_HasEmptyTools()
    {
        var catalog = new McpToolCatalog();

        Assert.NotNull(catalog.Tools);
        Assert.Empty(catalog.Tools);
    }

    [Fact]
    public void ToolDefinition_RoundTripsThroughSystemTextJson()
    {
        var definition = new McpToolDefinition
        {
            ServerKey = "github",
            ToolName = "create_issue",
            FullName = "mcp__github__create_issue",
            Description = "Create an issue",
            ParametersSchemaJson = """{"type":"object","properties":{}}""",
        };

        var json = JsonSerializer.Serialize(definition);
        var roundTripped = JsonSerializer.Deserialize<McpToolDefinition>(json);

        Assert.Equal(definition, roundTripped);
        Assert.Equal("mcp__github__create_issue", roundTripped!.FullName);
    }

    [Fact]
    public void ToolDefinition_RequiresApproval_DefaultsToFalse()
    {
        // Fail-safe default: a tool is read-only unless the admin EXPLICITLY classified it as
        // a mutation — never the other way around.
        var definition = new McpToolDefinition
        {
            ServerKey = "github",
            ToolName = "list_prs",
            FullName = "mcp__github__list_prs",
            Description = "List PRs",
            ParametersSchemaJson = "{}",
        };

        Assert.False(definition.RequiresApproval);
    }

    [Fact]
    public void ToolDefinition_RequiresApproval_RoundTripsThroughSystemTextJson()
    {
        var definition = new McpToolDefinition
        {
            ServerKey = "github",
            ToolName = "create_issue",
            FullName = "mcp__github__create_issue",
            Description = "Create an issue",
            ParametersSchemaJson = "{}",
            RequiresApproval = true,
        };

        var json = JsonSerializer.Serialize(definition);
        var roundTripped = JsonSerializer.Deserialize<McpToolDefinition>(json);

        Assert.Equal(definition, roundTripped);
        Assert.True(roundTripped!.RequiresApproval);
    }

    [Fact]
    public void Invocation_RoundTripsThroughSystemTextJson_WithIdentityFields()
    {
        var invocation = new McpToolInvocation
        {
            InvocationId = "0198f0c4b6a97b0eaad2c7a2f52f1a01",
            ServerKey = "github",
            ToolName = "create_issue",
            ArgumentsJson = "{}",
            ConversationId = "conv-1",
            ChannelId = "webchat-default",
            CorrelationId = "corr-7",
            WorkerId = "worker-3",
        };

        var json = JsonSerializer.Serialize(invocation);
        var roundTripped = JsonSerializer.Deserialize<McpToolInvocation>(json);

        Assert.Equal(invocation, roundTripped);
        Assert.Equal("0198f0c4b6a97b0eaad2c7a2f52f1a01", roundTripped!.InvocationId);
        Assert.Equal("worker-3", roundTripped.WorkerId);
    }

    [Fact]
    public void Result_RoundTripsThroughSystemTextJson_PreservingOutcomeEnums()
    {
        var result = McpToolResult.Unknown("inv-9", McpFailureKind.Transport, "transport lost mid-call");

        var json = JsonSerializer.Serialize(result);
        var roundTripped = JsonSerializer.Deserialize<McpToolResult>(json);

        Assert.Equal(result, roundTripped);
        Assert.Equal("inv-9", roundTripped!.InvocationId);
        Assert.Equal(McpToolOutcome.OutcomeUnknown, roundTripped.Outcome);
        Assert.Equal(McpFailureKind.Transport, roundTripped.FailureKind);
        Assert.True(roundTripped.IsError);
    }

    [Fact]
    public void Cancellation_RoundTripsThroughSystemTextJson()
    {
        var cancellation = new McpToolCancellation
        {
            InvocationId = "inv-9",
            Reason = "caller cancelled",
        };

        var json = JsonSerializer.Serialize(cancellation);
        var roundTripped = JsonSerializer.Deserialize<McpToolCancellation>(json);

        Assert.Equal(cancellation, roundTripped);
    }

    [Fact]
    public void Result_Disposition_DefaultsToCompleted_WithNoActionIdentity()
    {
        var result = McpToolResult.Ok("inv-1", "x");

        Assert.Equal(McpToolDisposition.Completed, result.Disposition);
        Assert.Null(result.ActionId);
        Assert.Null(result.ArgumentsHash);
    }

    [Fact]
    public void AwaitingApproval_Result_IsSuccessfulContent_NotAnError()
    {
        var result = McpToolResult.AwaitingApproval("inv-1", "act-1", "sha256:abc", """{"actionId":"act-1"}""");

        // The approval-required result is SUCCESSFUL tool content — never a retryable error.
        Assert.Equal(McpToolOutcome.Succeeded, result.Outcome);
        Assert.False(result.IsError);
        Assert.Equal(McpToolDisposition.AwaitingApproval, result.Disposition);
        Assert.Equal("act-1", result.ActionId);
        Assert.Equal("sha256:abc", result.ArgumentsHash);
        Assert.Equal("""{"actionId":"act-1"}""", result.Content);
    }

    [Fact]
    public void AwaitingApproval_Result_RoundTripsThroughSystemTextJson()
    {
        var result = McpToolResult.AwaitingApproval("inv-1", "act-1", "sha256:abc", "{}");

        var json = JsonSerializer.Serialize(result);
        var roundTripped = JsonSerializer.Deserialize<McpToolResult>(json);

        Assert.Equal(result, roundTripped);
        Assert.Equal(McpToolDisposition.AwaitingApproval, roundTripped!.Disposition);
        Assert.Equal("act-1", roundTripped.ActionId);
    }

    [Fact]
    public void ActionStatusTypes_RoundTripThroughSystemTextJson()
    {
        var request = new McpActionStatusRequest { ActionId = "act-1" };
        var response = new McpActionStatusResponse
        {
            Found = true,
            ActionId = "act-1",
            Status = "outcome_unknown",
            ArgumentsHash = "sha256:abc",
            ServerKey = "github",
            ToolName = "create_issue",
            Error = "transport lost",
            RemoteReference = "https://github.example/i/42",
        };

        Assert.Equal(request, JsonSerializer.Deserialize<McpActionStatusRequest>(JsonSerializer.Serialize(request)));
        Assert.Equal(response, JsonSerializer.Deserialize<McpActionStatusResponse>(JsonSerializer.Serialize(response)));
    }

    [Fact]
    public void ActionCancelTypes_RoundTripThroughSystemTextJson()
    {
        var request = new McpActionCancelRequest { ActionId = "act-1", ArgumentsHash = "sha256:abc" };
        var response = new McpActionCancelResponse { Accepted = true, Status = "cancelled" };

        Assert.Equal(request, JsonSerializer.Deserialize<McpActionCancelRequest>(JsonSerializer.Serialize(request)));
        Assert.Equal(response, JsonSerializer.Deserialize<McpActionCancelResponse>(JsonSerializer.Serialize(response)));
    }
}
