namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Request to query whether a given absolute path is inside a configured coding folder.
/// </summary>
public sealed record CodingFolderQueryRequest
{
    public required string AbsolutePath { get; init; }
}
