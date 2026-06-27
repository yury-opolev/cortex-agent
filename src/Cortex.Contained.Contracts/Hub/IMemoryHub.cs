namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Memory CRUD, search, configuration, and compaction methods exposed by the agent.
/// Part of the composed <see cref="IAgentHub"/> surface — these methods share the
/// single SignalR hub connection and route by method name.
/// </summary>
public interface IMemoryHub
{
    /// <summary>Clear all memories.</summary>
    Task ClearAllMemories();

    /// <summary>List all memories with pagination.</summary>
    Task<MemoryListResult> ListMemories(int limit, int offset);

    /// <summary>Get a single memory by ID.</summary>
    Task<MemoryItem?> GetMemory(string memoryId);

    /// <summary>Create a new memory via ingest.</summary>
    Task<MemoryItem?> CreateMemory(MemoryCreateRequest request);

    /// <summary>Update an existing memory.</summary>
    Task<MemoryItem?> UpdateMemory(MemoryUpdateRequest request);

    /// <summary>Delete a memory by ID.</summary>
    Task<bool> DeleteMemory(string memoryId);

    /// <summary>Search memories by semantic similarity.</summary>
    Task<IReadOnlyList<MemorySearchItem>> SearchMemories(MemorySearchRequest request);

    /// <summary>Get the current memory configuration.</summary>
    Task<MemoryConfig> GetMemoryConfig();

    /// <summary>Update memory configuration at runtime.</summary>
    Task UpdateMemoryConfig(MemoryConfig config);

    /// <summary>
    /// Run a memory dedup/compaction sweep across all memories.
    /// </summary>
    Task<CompactMemoriesResult> CompactMemories();

    /// <summary>
    /// Probes an embedding endpoint from the agent's network context (so Docker-internal
    /// service names like <c>http://embeddings:11434</c> resolve correctly). Sends a small
    /// embed request and validates the response dimension.
    /// </summary>
    /// <param name="endpoint">Base URL of the embedding service to probe.</param>
    /// <param name="apiKey">Optional Bearer token; null/empty means no Authorization header.</param>
    Task<EmbeddingProbeResult> TestEmbeddingEndpoint(string endpoint, string? apiKey);
}
