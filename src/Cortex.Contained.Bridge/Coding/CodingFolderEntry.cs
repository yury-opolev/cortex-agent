using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>One allowed coding folder and its permission ceiling.</summary>
public sealed class CodingFolderEntry
{
    public required string Path { get; set; }

    public string? Label { get; set; }

    public CodingPolicy DefaultPolicy { get; set; } = CodingPolicy.YoloSafe;
}
