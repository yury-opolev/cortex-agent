using System.Text;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Memory;

/// <summary>
/// The curated-memory keyword (BM25) index on the agent's SQLite engine (SQLite3 Multiple Ciphers):
/// folded title + content per memory, filters before ranking, physical erasure on delete and edit,
/// backfill of existing memories and rebuild on a fingerprint change.
/// </summary>
public sealed class MemoryKeywordIndexTests : IDisposable
{
    private const int Dimensions = 4;

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"memory-kw-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions options;
    private SqliteVecMemoryStore store;

    public MemoryKeywordIndexTests()
    {
        Directory.CreateDirectory(this.directory);
        this.options = new MemoryMcpOptions { DataDirectory = this.directory, Ollama = new OllamaOptions { Dimensions = Dimensions } };
        this.store = this.NewStore();
    }

    public void Dispose()
    {
        this.store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(this.directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a pooled handle may still be closing
        }
    }

    [Fact]
    public async Task KeywordSearch_FindsFoldedTitleAndContentWords()
    {
        await this.InitializeAsync();
        await this.StoreAsync("m1", "Trip planning", "We take the night train to København on Friday.");
        await this.StoreAsync("m2", "Groceries", "Buy milk and bread.");

        Assert.Equal(["m1"], await this.SearchAsync("kobenhavn"));
        Assert.Equal(["m1"], await this.SearchAsync("planning"));
        Assert.Equal(["m2"], await this.SearchAsync("\"milk and bread\""));
        Assert.Empty(await this.SearchAsync("\"bread and milk\""));
        Assert.Equal(["m1"], await this.SearchAsync("københ*"));
    }

    [Fact]
    public async Task KeywordSearch_TagAndExclusionFilters_ApplyBeforeRanking()
    {
        await this.InitializeAsync();
        for (var i = 0; i < 30; i++)
        {
            await this.StoreAsync($"c{i:D2}", null, "invoice invoice invoice invoice", ["common"]);
        }

        await this.StoreAsync("r1", null, "an invoice mentioned once among many other words here", ["rare"]);
        await this.StoreAsync("r2", null, "invoice and invoice again", ["rare"]);

        var tagged = await this.SearchAsync("invoice", filter: new MemorySearchFilter { Tags = ["RARE"] }, limit: 1);
        var excluded = await this.SearchAsync("invoice", filter: new MemorySearchFilter { Tags = ["rare"], ExcludeIds = new HashSet<string> { "r2" } });

        Assert.Equal(["r2"], tagged);
        Assert.Equal(["r1"], excluded);
    }

    [Fact]
    public async Task KeywordSearch_StemmedQuery_MatchesInflectedForms()
    {
        await this.InitializeAsync();
        await this.StoreAsync("m1", null, "She is travelling with two suitcases.");

        Assert.Empty(await this.SearchAsync("travels suitcase"));
        Assert.Equal(["m1"], await this.SearchAsync("travels suitcase", stemmed: true));
    }

    [Fact]
    [Trait("Category", "Integrity")]
    public async Task Delete_ErasesTheMemorysTokensFromTheDatabaseFile()
    {
        await this.InitializeAsync();
        await this.StoreAsync("m1", "zqxtitlemarker", "the zqxbodymarker appears once");
        await this.StoreAsync("m2", null, "unrelated memory");
        Assert.Equal(["m1"], await this.SearchAsync("zqxbodymarker"));
        Assert.True(this.DatabaseContains("zqxbody"));

        Assert.True(await this.store.DeleteMemoryAsync("m1", CancellationToken.None));

        Assert.Empty(await this.SearchAsync("zqxbodymarker"));
        Assert.False(this.DatabaseContains("zqxbody"));
        Assert.False(this.DatabaseContains("titlemark"));
        Assert.Equal(["m2"], await this.SearchAsync("unrelated"));
    }

    [Fact]
    [Trait("Category", "Integrity")]
    public async Task ContentAndTitleEdits_ReplaceTheIndexedTokens()
    {
        var ct = CancellationToken.None;
        await this.InitializeAsync();
        await this.StoreAsync("m1", "zqxoldtitle", "the zqxoldbody text");
        Assert.True(this.DatabaseContains("zqxold"));

        // MemoryService.UpdateAsync with new content deletes and re-stores the memory.
        await this.store.DeleteMemoryAsync("m1", ct);
        await this.StoreAsync("m1", "zqxoldtitle", "the zqxnewbody text");
        await this.store.UpdateMetadataAsync("m1", "zqxnewtitle", null, DateTimeOffset.UtcNow, ct);

        Assert.Empty(await this.SearchAsync("zqxoldbody"));
        Assert.Empty(await this.SearchAsync("zqxoldtitle"));
        Assert.Equal(["m1"], await this.SearchAsync("zqxnewbody zqxnewtitle"));
        Assert.False(this.DatabaseContains("zqxold"));
    }

    [Fact]
    public async Task Initialize_BackfillsMemoriesStoredBeforeTheIndexExisted()
    {
        await this.InitializeAsync();
        await this.StoreAsync("m1", "Old memory", "stored before the keyword index existed");
        this.Execute("DROP TABLE memories_fts; DROP TABLE memories_fts_map; DROP TABLE memory_meta;");

        this.Reopen();
        await this.InitializeAsync();

        Assert.Equal(["m1"], await this.SearchAsync("existed"));
    }

    [Fact]
    public async Task Initialize_FingerprintChanged_RebuildsTheIndex()
    {
        await this.InitializeAsync();
        await this.StoreAsync("m1", null, "first memory");
        this.Execute("INSERT INTO memories_fts(memories_fts) VALUES('delete-all'); DELETE FROM memories_fts_map; UPDATE memory_meta SET Value = 'schema=0' WHERE Key = 'keyword_index';");

        this.Reopen();
        await this.InitializeAsync();

        Assert.Equal(["m1"], await this.SearchAsync("first"));
        Assert.Null(this.store.KeywordIndexUnavailableReason);
    }

    private SqliteVecMemoryStore NewStore() =>
        new(Options.Create(this.options), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance);

    private Task InitializeAsync() => this.store.InitializeAsync(CancellationToken.None);

    private void Reopen()
    {
        this.store.Dispose();
        SqliteConnection.ClearAllPools();
        this.store = this.NewStore();
    }

    private Task StoreAsync(string id, string? title, string content, List<string>? tags = null)
    {
        var now = DateTimeOffset.UtcNow;
        var chunk = new ChunkRecord { MemoryId = id, ChunkIndex = 0, Length = content.Length, Title = title, Tags = tags ?? [], CreatedAt = now, UpdatedAt = now };
        return this.store.StoreMemoryAsync(id, content, [chunk], [[1, 0, 0, 0]], CancellationToken.None);
    }

    private async Task<List<string>> SearchAsync(string query, MemorySearchFilter? filter = null, int limit = 10, bool stemmed = false)
    {
        var hits = await this.store.KeywordSearchAsync(KeywordQuery.Build(query, stemmed)!, limit, filter ?? MemorySearchFilter.None, CancellationToken.None);
        return hits.Select(h => h.MemoryId).ToList();
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={this.options.DatabasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // FTS5 leaves prefix-compress sorted terms, so only the first term sharing a prefix is stored whole:
    // checks use a marker prefix ("zqxbody") or a suffix fragment ("titlemark"), never a whole later term.
    private bool DatabaseContains(string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        foreach (var path in new[] { this.options.DatabasePath, this.options.DatabasePath + "-journal", this.options.DatabasePath + "-wal" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (copy.ToArray().AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
