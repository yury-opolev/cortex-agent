using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Provides CRUD and search operations for the memory management web UI and
/// the <see cref="MemoryCompactionService"/>. Wraps the underlying
/// <see cref="MemoryMcp.Core.Services.IMemoryService"/> with pagination
/// support backed by direct SQLite queries.
/// </summary>
public interface IMemoryManagementService
{
    /// <summary>Lists all memories with pagination.</summary>
    Task<MemoryListResult> ListAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>Get a single memory by ID.</summary>
    Task<MemoryItem?> GetAsync(string memoryId, CancellationToken cancellationToken = default);

    /// <summary>Create a new memory via ingest.</summary>
    /// <param name="content">Memory content text.</param>
    /// <param name="title">Optional title.</param>
    /// <param name="tags">Optional tags.</param>
    /// <param name="force">When <c>true</c> (default), bypasses duplicate detection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MemoryItem?> CreateAsync(string content, string? title, List<string>? tags, bool force = true, CancellationToken cancellationToken = default);

    /// <summary>Update an existing memory.</summary>
    Task<MemoryItem?> UpdateAsync(string memoryId, string? content, string? title, List<string>? tags, CancellationToken cancellationToken = default);

    /// <summary>Delete a memory by ID.</summary>
    Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken = default);

    /// <summary>Search memories by semantic similarity.</summary>
    Task<IReadOnlyList<MemorySearchItem>> SearchAsync(
        string query, int limit = 10, float? minScore = null, List<string>? tags = null,
        CancellationToken cancellationToken = default);
}
