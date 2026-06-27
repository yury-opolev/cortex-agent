using Cortex.Contained.Agent.Host.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

internal sealed class CodingSessionListTool : IAgentTool
{
    private readonly ICodingAgent agent;

    public CodingSessionListTool(ICodingAgent agent)
    {
        this.agent = agent;
    }

    public string Name => "coding_session_list";

    public string Description =>
        "List all known external coding-agent sessions (live and recently ended) across the entire host.";

    public string ParametersSchema => """
        { "type": "object", "properties": {} }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var sessions = await this.agent.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            return CodingToolBase.Ok(new
            {
                sessions = sessions.Select(CodingToolBase.SnapshotPayload),
            });
        }
        catch (Exception ex)
        {
            // CodingInvokeException carries the stable coda_* code; everything else is internal_error.
            return CodingToolBase.FromException(ex);
        }
    }
}
