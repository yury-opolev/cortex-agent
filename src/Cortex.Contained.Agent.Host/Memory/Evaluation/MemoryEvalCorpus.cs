using System.Diagnostics;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Memory.Evaluation;

/// <summary>
/// A private copy of the curated-memory store (<c>memory.db</c> + content files) for one evaluation
/// command. The live store is never touched: the copy is opened with its own <see cref="SqliteVecMemoryStore"/>
/// (which backfills the keyword index in the copy) and searched with <see cref="HybridMemorySearch"/> per
/// configuration. The copy holds raw memory text (D6), so <see cref="DisposeAsync"/> deletes it: no raw
/// copy outlives the command, and outputs beside it are ids and numbers only.
/// </summary>
public sealed class MemoryEvalCorpus : IAsyncDisposable
{
    /// <summary>Memories returned per query (the metrics read the first 10).</summary>
    public const int ResultLimit = 10;

    private readonly string corpusDirectory;
    private readonly MemoryMcpOptions options;
    private readonly SqliteVecMemoryStore store;
    private readonly HybridMemorySearch search;

    private MemoryEvalCorpus(string corpusDirectory, MemoryMcpOptions options, SqliteVecMemoryStore store, IEmbeddingService embedding)
    {
        this.corpusDirectory = corpusDirectory;
        this.options = options;
        this.store = store;
        this.search = new HybridMemorySearch(embedding, store, new StaticOptionsMonitor(options));
    }

    /// <summary>
    /// Copies the store under <paramref name="sourceDirectory"/> (named like <paramref name="live"/>) into
    /// <paramref name="runDirectory"/><c>/corpus</c> and opens it. Fails when the keyword index cannot be built.
    /// </summary>
    public static async Task<MemoryEvalCorpus> CreateAsync(
        string sourceDirectory, string runDirectory, MemoryMcpOptions live, IEmbeddingService embedding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(embedding);
        var sourceDb = Path.Combine(sourceDirectory, live.DatabaseFileName);
        if (!File.Exists(sourceDb))
        {
            throw new InvalidOperationException($"No memory database at {sourceDb}.");
        }

        var corpusDirectory = Path.Combine(runDirectory, "corpus");
        var options = new MemoryMcpOptions
        {
            DataDirectory = corpusDirectory,
            DatabaseFileName = live.DatabaseFileName,
            MemoriesSubdirectory = live.MemoriesSubdirectory,
            SearchMaxContentLength = live.SearchMaxContentLength,
            Ollama = live.Ollama,
            Search = live.Search,
        };

        try
        {
            Directory.CreateDirectory(options.MemoriesDirectory);
            using (var source = new SqliteConnection($"Data Source={sourceDb};Mode=ReadOnly;Pooling=False"))
            using (var target = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }

            var sourceMemories = Path.Combine(sourceDirectory, live.MemoriesSubdirectory);
            if (Directory.Exists(sourceMemories))
            {
                foreach (var file in Directory.EnumerateFiles(sourceMemories, "*.memory.data"))
                {
                    File.Copy(file, Path.Combine(options.MemoriesDirectory, Path.GetFileName(file)));
                }
            }
        }
        catch
        {
            DeleteCopy(corpusDirectory);
            throw;
        }

        var store = new SqliteVecMemoryStore(Options.Create(options), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance);
        var corpus = new MemoryEvalCorpus(corpusDirectory, options, store, embedding);
        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (store.KeywordIndexUnavailableReason is { } reason)
            {
                throw new InvalidOperationException(reason);
            }
        }
        catch
        {
            await corpus.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return corpus;
    }

    /// <summary>Runs <paramref name="config"/> for <paramref name="query"/> (oracle or model-written text and tags).</summary>
    public async Task<MemoryRetrievalRun> SearchAsync(MemoryAblationConfig config, MemoryEvalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(query);
        var model = config.UseModelQuery ? query.ModelQuery : null;
        var watch = Stopwatch.StartNew();
        var result = await this.search.SearchAsync(
            new HybridSearchRequest
            {
                Query = model?.Text ?? query.Text,
                Mode = config.Mode,
                Limit = ResultLimit,
                Filter = new MemorySearchFilter { Tags = model is not null ? model.Tags : query.Tags },
                Settings = config.Apply(this.options.Search),
            },
            cancellationToken).ConfigureAwait(false);
        var latency = watch.Elapsed.TotalMilliseconds;
        var returned = result.Hits
            .Select(h => new ReturnedMemory(h.Memory.MemoryId, WordsShown(h.Memory.Content, this.options.SearchMaxContentLength)))
            .ToList();
        return new MemoryRetrievalRun(returned, result.TopSimilarity ?? 0, latency);
    }

    /// <summary>Every memory with its title, tags and content (query seeds and the paraphrase guard; never written to reports).</summary>
    public async Task<IReadOnlyList<MemorySeedSource>> ListMemoriesAsync(CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        using (var connection = new SqliteConnection($"Data Source={this.options.DatabasePath};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT MemoryId FROM chunks WHERE ChunkIndex = 0 ORDER BY MemoryId";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var memories = new List<MemorySeedSource>();
        foreach (var id in ids)
        {
            if (await this.store.GetMemoryAsync(id, cancellationToken).ConfigureAwait(false) is { } memory)
            {
                memories.Add(new MemorySeedSource(memory.MemoryId, memory.Title, memory.Tags, memory.Content));
            }
        }

        return memories;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        this.store.Dispose();
        DeleteCopy(this.corpusDirectory);
        return ValueTask.CompletedTask;
    }

    private static void DeleteCopy(string directory)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // already gone
        }
    }

    private static int WordsShown(string content, int maxContentLength) =>
        KeywordTokenizer.CountTokens(TextFolder.Fold(content.Length > maxContentLength ? content[..maxContentLength] : content));

    private sealed class StaticOptionsMonitor(MemoryMcpOptions value) : IOptionsMonitor<MemoryMcpOptions>
    {
        public MemoryMcpOptions CurrentValue => value;

        public MemoryMcpOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<MemoryMcpOptions, string?> listener) => null;
    }
}
