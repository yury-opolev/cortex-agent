using Cortex.Contained.Agent.Host.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

internal sealed class CodingFoldersListTool : IAgentTool
{
    private readonly ICodingAgent agent;

    public CodingFoldersListTool(ICodingAgent agent)
    {
        this.agent = agent;
    }

    public string Name => "coding_folders_list";

    public string Description =>
        "List the host folders the coding agent is allowed to work in. Use this to answer " +
        "'what can you work on?' and to resolve a project the user names loosely.";

    public string ParametersSchema => """
        { "type": "object", "properties": {} }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var list = await this.agent.ListAllowedFoldersAsync(cancellationToken).ConfigureAwait(false);
            return CodingToolBase.Ok(new
            {
                folders = list.Folders.Select(f => new { absolutePath = f.AbsolutePath, label = f.Label }),
            });
        }
        catch (Exception ex)
        {
            // CodingInvokeException carries the stable coda_* code; everything else is internal_error.
            return CodingToolBase.FromException(ex);
        }
    }
}
