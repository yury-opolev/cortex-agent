using System.Text.Json;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Contracts.Tests.Mcp;

public class McpDtoTests
{
    [Fact]
    public void Ok_Result_IsNotError_AndCarriesContent()
    {
        var result = McpToolResult.Ok("x");

        Assert.False(result.IsError);
        Assert.Equal("x", result.Content);
        Assert.False(result.NeedsAuth);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Fail_Result_IsError_AndSurfacesNeedsAuth()
    {
        var result = McpToolResult.Fail("e", needsAuth: true);

        Assert.True(result.IsError);
        Assert.Equal("e", result.Error);
        Assert.True(result.NeedsAuth);
        Assert.Equal(string.Empty, result.Content);
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
    public void Invocation_RoundTripsThroughSystemTextJson()
    {
        var invocation = new McpToolInvocation
        {
            ServerKey = "github",
            ToolName = "create_issue",
            ArgumentsJson = "{}",
            ConversationId = "conv-1",
        };

        var json = JsonSerializer.Serialize(invocation);
        var roundTripped = JsonSerializer.Deserialize<McpToolInvocation>(json);

        Assert.Equal(invocation, roundTripped);
    }
}
