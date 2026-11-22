using System.Diagnostics;
using Cortex.Contained.Agent.Host.Reranking;
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

    private const string LockFileName = "corpus.lock";

    private readonly string corpusDirectory;
    private readonly FileStream runLock;
    private readonly MemoryMcpOptions options;
    private readonly SqliteVecMemoryStore store;
    private readonly HybridMemorySearch search;
    private readonly IRerankStage? reranker;

    private MemoryEvalCorpus(string corpusDirectory, FileStream runLock, MemoryMcpOptions options, SqliteVecMemoryStore store, IEmbeddingService embedding, IRerankStage? reranker)
    {
        this.reranker = reranker;
        this.corpusDirectory = corpusDirectory;
        this.runLock = runLock;
        this.options = options;
        this.store = store;
        this.search = new HybridMemorySearch(embedding, store, new StaticOptionsMonitor(options));
    }

    /// <summary>
    /// Copies the store under <paramref name="sourceDirectory"/> (named like <paramref name="live"/>) into
    /// <paramref name="runDirectory"/><c>/corpus</c> and opens it. Fails when the keyword index cannot be built.
    /// </summary>
    public static async Task<MemoryEvalCorpus> CreateAsync(
        string sourceDirectory, string runDirectory, MemoryMcpOptions live, IEmbeddingService embedding, CancellationToken cancellationToken, IRerankStage? reranker = null)
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

        // Held for the life of the copy: a later command sweeps a copy only when nobody holds this lock.
        Directory.CreateDirectory(runDirectory);
        var runLock = new FileStream(Path.Combine(runDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
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
            runLock.Dispose();
            throw;
        }

        var store = new SqliteVecMemoryStore(Options.Create(options), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance);
        var corpus = new MemoryEvalCorpus(corpusDirectory, runLock, options, store, embedding, reranker);
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
        var rerank = config.RerankBudget();
        if (rerank is not null && this.reranker is null)
        {
            throw new InvalidOperationException($"Configuration '{config.Name}' reranks, but this run has no reranker: evaluate rerank configurations with 'rerank-curve' or 'gate-r'.");
        }

        var model = config.UseModelQuery ? query.ModelQuery : null;
        var text = model?.Text ?? query.Text;
        var watch = Stopwatch.StartNew();
        var result = await this.search.SearchAsync(
            new HybridSearchRequest
            {
                Query = text,
                Mode = config.Mode,
                Limit = HybridMemoryReranking.CandidateLimit(ResultLimit, rerank),
                Filter = new MemorySearchFilter { Tags = model is not null ? model.Tags : query.Tags },
                Settings = config.Apply(this.options.Search),
            },
            cancellationToken).ConfigureAwait(false);
        RerankOutcome? outcome = null;
        if (rerank is not null)
        {
            (result, outcome, _) = await HybridMemoryReranking.ApplyAsync(this.reranker, text, result, ResultLimit, rerank, cancellationToken).ConfigureAwait(false);
        }

        var latency = watch.Elapsed.TotalMilliseconds;
        if (result.Fallback is { } fallback)
        {
            // A degraded run would score a different configuration than the one named in the report.
            throw new InvalidOperationException($"Configuration '{config.Name}' fell back to {result.EffectiveMode} for query '{query.Id}': {fallback}. Fix the cause and run again.");
        }
        var returned = result.Hits
            .Select(h => new ReturnedMemory(h.Memory.MemoryId, WordsShown(h.Memory.Content, this.options.SearchMaxContentLength)))
            .ToList();
        return new MemoryRetrievalRun(returned, result.TopSimilarity ?? 0, latency) { Rerank = outcome };
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
        this.runLock.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Deletes the raw copies (<c>runs/*/corpus</c>) that runs killed before disposal (SIGKILL, power loss)
    /// left behind. A copy whose run still holds its lock is in use and is kept; reports and verdicts beside
    /// the copy are kept too. Returns the number of copies removed.
    /// </summary>
    public static int SweepAbandoned(string runsDirectory)
    {
        if (!Directory.Exists(runsDirectory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var run in Directory.EnumerateDirectories(runsDirectory))
        {
            var corpus = Path.Combine(run, "corpus");
            if (!Directory.Exists(corpus))
            {
                continue;
            }

            try
            {
                using var gate = new FileStream(Path.Combine(run, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                DeleteCopy(corpus);
                removed++;
            }
            catch (IOException)
            {
                // in use by a running command, or not deletable now: left for the next sweep
            }
            catch (UnauthorizedAccessException)
            {
                // same: left for the next sweep
            }
        }

        return removed;
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
