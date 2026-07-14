namespace Cortex.Contained.Bridge.Mcp.Actions;

/// <summary>
/// Maps <see cref="McpActionState"/> to the snake_case status strings used on every external
/// surface (REST API, agent tools, awaiting-approval tool content) — identical to the store's
/// persisted status column values so operators see one vocabulary everywhere.
/// </summary>
public static class McpActionWireStatus
{
    /// <summary>The snake_case wire name of <paramref name="state"/> (e.g. <c>outcome_unknown</c>).</summary>
    public static string From(McpActionState state) => state switch
    {
        McpActionState.Proposed => "proposed",
        McpActionState.Approved => "approved",
        McpActionState.Rejected => "rejected",
        McpActionState.Dispatching => "dispatching",
        McpActionState.Succeeded => "succeeded",
        McpActionState.Failed => "failed",
        McpActionState.OutcomeUnknown => "outcome_unknown",
        McpActionState.ReconciledSucceeded => "reconciled_succeeded",
        McpActionState.ReconciledFailed => "reconciled_failed",
        McpActionState.Expired => "expired",
        McpActionState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown MCP action state."),
    };
}
