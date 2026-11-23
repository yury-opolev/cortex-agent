namespace Cortex.Contained.Contracts.Hub;

/// <summary>A frozen evaluation corpus version (sanitised corpus + datasets + plans + registration hashes).</summary>
public sealed record FrozenCorpusVersionDto
{
    /// <summary><c>v1</c>, <c>v2</c>, …</summary>
    public required string Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Oldest message of the corpus (D3 compares purges with this range).</summary>
    public DateTimeOffset CorpusFrom { get; init; }

    public DateTimeOffset CorpusTo { get; init; }

    public int Messages { get; init; }

    public int Memories { get; init; }

    public int HistoryQueries { get; init; }

    public int MemoryQueries { get; init; }

    /// <summary>File name of the export package and of its attachment in SafeExchange (<c>cortex-eval-corpus-v1.cxpkg</c>).</summary>
    public required string PackageFileName { get; init; }

    /// <summary>When the package was last exported, if ever.</summary>
    public DateTimeOffset? ExportedAt { get; init; }

    /// <summary>True when the owner recorded the upload to SafeExchange.</summary>
    public bool Uploaded { get; init; }

    /// <summary>True for a version imported from a package (evaluation elsewhere).</summary>
    public bool Imported { get; init; }
}

/// <summary>An off-machine package that must be deleted because a purge destroyed its version (D3).</summary>
public sealed record OffsiteDeletionDto
{
    public required string Version { get; init; }

    /// <summary>The SafeExchange secret the package is attached to (always <c>cortex-recovery-kit</c>).</summary>
    public required string Secret { get; init; }

    /// <summary>The attachment's file name in that secret.</summary>
    public required string PackageFileName { get; init; }

    /// <summary>The attachment's contentId when the owner recorded it after the upload.</summary>
    public string? ContentId { get; init; }

    public DateTimeOffset DestroyedAt { get; init; }
}

/// <summary>Frozen corpora, export packages and pending off-machine deletions.</summary>
public sealed record FrozenCorpusStatusDto
{
    public IReadOnlyList<FrozenCorpusVersionDto> Versions { get; init; } = [];

    /// <summary>Export packages present in the agent (passphrase-encrypted), by file name.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>Off-machine copies the owner still has to delete from SafeExchange.</summary>
    public IReadOnlyList<OffsiteDeletionDto> PendingOffsiteDeletions { get; init; } = [];
}

/// <summary>A chunk of an export package (passphrase-encrypted bytes).</summary>
public sealed record EvaluationPackageChunkDto
{
    /// <summary>Total package length.</summary>
    public long Length { get; init; }

    public byte[] Data { get; init; } = [];

    public string? Error { get; init; }
}
