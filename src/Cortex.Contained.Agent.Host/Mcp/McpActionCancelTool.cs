using System.Text.Json;
using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// Native agent tool <c>mcp_action_cancel</c>: cancels one approval-gated MCP action, bound to
/// its exact canonical-argument hash. Proposed/approved actions cancel immediately; a
/// dispatching action only records the request and asks the in-flight invocation to cancel —
/// if the mutation already began remotely the action resolves to <c>outcome_unknown</c>, never
/// <c>cancelled</c>.
/// </summary>
public sealed partial class McpActionCancelTool : IAgentTool
{
    private readonly IMcpGateway gateway;
    private readonly ILogger<McpActionCancelTool> logger;

    public McpActionCancelTool(IMcpGateway gateway, ILogger<McpActionCancelTool> logger)
    {
        this.gateway = gateway;
        this.logger = logger;
    }

    public string Name => "mcp_action_cancel";

    public string Description =>
        "Cancel a pending MCP mutation action by its action id and exact arguments hash. A "
        + "proposed or approved action is cancelled immediately; an action already dispatching "
        + "may end as outcome_unknown if the remote call had begun — check mcp_action_status.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action_id": {
              "type": "string",
              "description": "The MCP action id to cancel"
            },
            "arguments_hash": {
              "type": "string",
              "description": "The action's exact canonical arguments hash (sha256:...)"
            }
          },
          "required": ["action_id", "arguments_hash"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string actionId;
        string argumentsHash;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            actionId = doc.RootElement.TryGetProperty("action_id", out var idValue)
                ? idValue.GetString() ?? string.Empty
                : string.Empty;
            argumentsHash = doc.RootElement.TryGetProperty("arguments_hash", out var hashValue)
                ? hashValue.GetString() ?? string.Empty
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

        if (string.IsNullOrWhiteSpace(argumentsHash))
        {
            return AgentToolResult.Fail("Missing required parameter: arguments_hash");
        }

        var response = await this.gateway.CancelActionAsync(actionId, argumentsHash, cancellationToken).ConfigureAwait(false);
        this.LogCancelRequested(actionId, response.Accepted, response.Status);

        if (!response.Accepted)
        {
            return AgentToolResult.Fail(response.Error ?? $"MCP action '{actionId}' could not be cancelled.");
        }

        var status = response.Status ?? "unknown";
        var note = status == "dispatching"
            ? " The action was already dispatching: cancellation of the in-flight call was requested, but the "
              + "mutation may still have executed — check mcp_action_status; it will NOT be reported as cancelled "
              + "if the remote call had begun."
            : string.Empty;
        return AgentToolResult.Ok($"Cancel accepted for action {actionId}. Current status: {status}.{note}");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[mcp_action_cancel] {ActionId}: accepted={Accepted} status={Status}")]
    private partial void LogCancelRequested(string actionId, bool accepted, string? status);
}
