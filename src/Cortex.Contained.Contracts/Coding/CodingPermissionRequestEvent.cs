namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Push event when the coding agent requests permission for a tool (coda <c>request/permission</c>).
/// The session blocks until <see cref="CodingRespondRequest"/> arrives.
/// </summary>
public sealed record CodingPermissionRequestEvent
{
    public required string SessionId { get; init; }

    public required string RequestId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>Human-readable preview of the tool input (coda <c>inputPreview</c>).</summary>
    public required string InputPreview { get; init; }
}
