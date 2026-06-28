using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// A dynamic <see cref="IAgentTool"/> built from a pushed <see cref="McpToolDefinition"/>.
/// Its name/description/schema mirror the definition; execution routes through the
/// <see cref="IMcpGateway"/> to the Bridge's MCP host. To the LLM it is indistinguishable
/// from a built-in tool.
/// </summary>
public sealed partial class McpProxyTool : IAgentTool
{
    private readonly McpToolDefinition definition;
    private readonly IMcpGateway gateway;
    private readonly ILogger<McpProxyTool> logger;

    public McpProxyTool(McpToolDefinition definition, IMcpGateway gateway, ILogger<McpProxyTool> logger)
    {
        this.definition = definition;
        this.gateway = gateway;
        this.logger = logger;
    }

    /// <inheritdoc />
    public string Name => this.definition.FullName;

    /// <inheritdoc />
    public string Description => this.definition.Description;

    /// <inheritdoc />
    public string ParametersSchema => this.definition.ParametersSchemaJson;

    /// <inheritdoc />
    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        this.LogInvoking(this.definition.ServerKey, this.definition.ToolName);

        var result = await this.gateway.InvokeAsync(
            this.definition.ServerKey,
            this.definition.ToolName,
            argumentsJson,
            context.ConversationId,
            cancellationToken).ConfigureAwait(false);

        this.LogOutcome(this.definition.ServerKey, this.definition.ToolName, result.IsError);

        if (result.IsError)
        {
            var error = result.Error ?? "MCP tool invocation failed.";
            if (result.NeedsAuth)
            {
                return AgentToolResult.Fail($"needs authorization: {error}");
            }

            return AgentToolResult.Fail(error);
        }

        return AgentToolResult.Ok(result.Content);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Invoking MCP tool {ServerKey}/{ToolName}")]
    private partial void LogInvoking(string serverKey, string toolName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MCP tool {ServerKey}/{ToolName} completed: isError={IsError}")]
    private partial void LogOutcome(string serverKey, string toolName, bool isError);
}
