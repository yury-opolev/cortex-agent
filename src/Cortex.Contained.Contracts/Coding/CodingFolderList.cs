namespace Cortex.Contained.Contracts.Coding;

/// <summary>Wrapper for the allowed-folders list to keep the SignalR contract symmetric.</summary>
public sealed record CodingFolderList
{
    public required IReadOnlyList<CodingFolderInfo> Folders { get; init; }
}
