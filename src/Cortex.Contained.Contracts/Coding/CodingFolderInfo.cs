namespace Cortex.Contained.Contracts.Coding;

/// <summary>One allowed coding folder, surfaced to the agent for the folders-list tool.</summary>
public sealed record CodingFolderInfo
{
    public required string AbsolutePath { get; init; }

    public string? Label { get; init; }
}
