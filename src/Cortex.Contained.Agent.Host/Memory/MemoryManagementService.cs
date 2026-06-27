using System.Text.Json;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Wraps <see cref="IMemoryService"/> with additional list-all capability
/// by querying the SQLite chunks table directly. Bridges the gap between
/// the MemoryMcp.Core library (which has no ListAsync) and the memory
/// management web UI which needs pagination.
/// </summary>
public sealed class MemoryManagementService : IMemoryManagementService, IDisposable
{
    private readonly IMemoryService memoryService;
    private readonly MemoryMcpOptions options;
    private readonly ILogger<MemoryManagementService> logger;
    private SqliteConnection? readConnection;
    private bool disposed;

    public MemoryManagementService(
        IMemoryService memoryService,
        IOptions<MemoryMcpOptions> options,
        ILogger<MemoryManagementService> logger)
    {
        this.memoryService = memoryService;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <summary>
    /// Lists all memories with pagination, ordered by most recently updated first.
    /// Uses a separate read-only SQLite connection to avoid contention with the main store.
    /// </summary>
    public async Task<MemoryListResult> ListAsync(int limit = 50, int offset = 0, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Get total count
        using var countCmd = this.readConnection!.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(DISTINCT MemoryId) FROM chunks";
        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

        // Get paginated list (metadata from chunk index 0)
        using var listCmd = this.readConnection.CreateCommand();
        listCmd.CommandText = """
            SELECT MemoryId, Title, Tags, CreatedAt, UpdatedAt
            FROM chunks
            WHERE ChunkIndex = 0
            ORDER BY UpdatedAt DESC
            LIMIT @limit OFFSET @offset
            """;
        listCmd.Parameters.AddWithValue("@limit", limit);
        listCmd.Parameters.AddWithValue("@offset", offset);

        var items = new List<MemoryItem>();
        using var reader = await listCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var memoryId = reader.GetString(0);
            var title = reader.IsDBNull(1) ? null : reader.GetString(1);
            var tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [];
            var createdAt = DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture);
            var updatedAt = DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture);

            // Read content from file
            var contentPath = Path.Combine(this.options.MemoriesDirectory, $"{memoryId}.memory.data");
            var content = File.Exists(contentPath)
                ? await File.ReadAllTextAsync(contentPath, cancellationToken).ConfigureAwait(false)
                : "(content file missing)";

            items.Add(new MemoryItem
            {
                MemoryId = memoryId,
                Title = title,
                Content = content,
                Tags = tags,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
            });
        }

        return new MemoryListResult
        {
            Items = items,
            TotalCount = totalCount,
        };
    }

    /// <summary>Get a single memory by ID.</summary>
    public async Task<MemoryItem?> GetAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        var result = await this.memoryService.GetAsync(memoryId, cancellationToken).ConfigureAwait(false);
        return result is null ? null : ToMemoryItem(result);
    }

    /// <summary>Create a new memory via ingest.</summary>
    public async Task<MemoryItem?> CreateAsync(string content, string? title, List<string>? tags, bool force = true, CancellationToken cancellationToken = default)
    {
        var ingestResult = await this.memoryService.IngestAsync(content, title, tags, force, cancellationToken).ConfigureAwait(false);
        if (!ingestResult.Success || ingestResult.MemoryId is null)
        {
            return null;
        }

        var result = await this.memoryService.GetAsync(ingestResult.MemoryId, cancellationToken).ConfigureAwait(false);
        return result is null ? null : ToMemoryItem(result);
    }

    /// <summary>Update an existing memory.</summary>
    public async Task<MemoryItem?> UpdateAsync(string memoryId, string? content, string? title, List<string>? tags, CancellationToken cancellationToken = default)
    {
        var result = await this.memoryService.UpdateAsync(memoryId, content, title, tags, cancellationToken).ConfigureAwait(false);
        return result is null ? null : ToMemoryItem(result);
    }

    /// <summary>Delete a memory by ID.</summary>
    public Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        return this.memoryService.DeleteAsync(memoryId, cancellationToken);
    }

    /// <summary>Search memories by semantic similarity.</summary>
    public async Task<IReadOnlyList<MemorySearchItem>> SearchAsync(
        string query, int limit = 10, float? minScore = null, List<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var results = await this.memoryService.SearchAsync(query, limit, minScore, tags, cancellationToken).ConfigureAwait(false);
        return results.Select(r => new MemorySearchItem
        {
            MemoryId = r.MemoryId,
            Title = r.Title,
            Content = r.Content,
            Tags = r.Tags,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            Score = r.Score,
        }).ToList();
    }

    private static MemoryItem ToMemoryItem(MemoryResult result) => new()
    {
        MemoryId = result.MemoryId,
        Title = result.Title,
        Content = result.Content,
        Tags = result.Tags,
        CreatedAt = result.CreatedAt,
        UpdatedAt = result.UpdatedAt,
    };

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (this.readConnection is not null && this.readConnection.State == System.Data.ConnectionState.Open)
        {
            return;
        }

        this.readConnection?.Dispose();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        this.readConnection = new SqliteConnection(connectionString);
        await this.readConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            this.readConnection?.Dispose();
            this.disposed = true;
        }
    }
}
