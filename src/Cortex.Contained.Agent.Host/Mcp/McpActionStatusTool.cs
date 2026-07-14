using System.Text.Json;
using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// Native agent tool <c>mcp_action_status</c>: looks up the current status of one
/// approval-gated MCP action on the Bridge by its durable action id. This is the safe way to
/// follow up on an awaiting-approval or outcome-unknown mutation — never by repeating the
/// original tool call.
/// </summary>
public sealed partial class McpActionStatusTool : IAgentTool
{
    private static readonly JsonSerializerOptions ContentSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IMcpGateway gateway;
    private readonly ILogger<McpActionStatusTool> logger;

    public McpActionStatusTool(IMcpGateway gateway, ILogger<McpActionStatusTool> logger)
    {
        this.gateway = gateway;
        this.logger = logger;
    }

    public string Name => "mcp_action_status";

    public string Description =>
        "Check the status of a pending or completed MCP mutation action by its action id "
        + "(returned when a mutating MCP tool call was recorded for approval). Use this instead "
        + "of repeating a mutating tool call.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action_id": {
              "type": "string",
              "description": "The MCP action id to look up"
            }
          },
          "required": ["action_id"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string actionId;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            actionId = doc.RootElement.TryGetProperty("action_id", out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return AgentToolResult.Fail($"Invalid arguments: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(actionId))
        {
            return AgentToolResult.Fail("Missing required parameter: action_id");
        }

        var response = await this.gateway.GetActionStatusAsync(actionId, cancellationToken).ConfigureAwait(false);
        this.LogStatusFetched(actionId, response.Found, response.Status);

        if (!response.Found)
        {
            return AgentToolResult.Fail(response.Error ?? $"No MCP action '{actionId}'.");
        }

        return AgentToolResult.Ok(JsonSerializer.Serialize(response, ContentSerializerOptions));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "[mcp_action_status] {ActionId}: found={Found} status={Status}")]
    private partial void LogStatusFetched(string actionId, bool found, string? status);
}
