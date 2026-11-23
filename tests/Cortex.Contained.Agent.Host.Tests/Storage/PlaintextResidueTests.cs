using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>Plaintext residue outside the encrypted stores: evaluation copies and deleted rows of the small stores.</summary>
[Trait("Category", "Integrity")]
public sealed class PlaintextResidueTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"residue-{Guid.NewGuid():N}");

    public PlaintextResidueTests()
    {
        Directory.CreateDirectory(this.root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CORTEX_STATE_PATH"] = this.root,
            ["CORTEX_DATA_PATH"] = this.root,
            ["MemoryMcp:DataDirectory"] = Path.Combine(this.root, "memory"),
        })
        .Build();

    [Fact]
    public async Task EvaluationCommands_RefuseOnlyWhileEncryptionIsOn()
    {
        using var output = new StringWriter();
        Assert.False(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), ["history-eval", "ablate"], output));

        new StorageEncryptionMarker { KeyFingerprint = "fp" }.Write(Path.Combine(this.root, StorageEncryptionMarker.FileName));

        Assert.True(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), ["history-eval", "ablate", "--split", "dev"], output));
        Assert.Contains("Invoke-CortexEval.ps1 -Tool history-eval -- ablate --split dev", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("history-eval", "kappa")]
    [InlineData("memory-eval", "kappa")]
    [InlineData("history-smoke", "embedder-check")]
    [InlineData("history-smoke", "reranker-check")]
    public async Task EvaluationCommands_StoreFreeCommandsRunWhileEncrypted(string tool, string command)
    {
        new StorageEncryptionMarker { KeyFingerprint = "fp" }.Write(Path.Combine(this.root, StorageEncryptionMarker.FileName));
        using var output = new StringWriter();

        Assert.False(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), [tool, command], output));
    }

    /// <summary>
    /// subagents.db, todos.db and tasks.db rely on the SQLite3 Multiple Ciphers build compiling
    /// <c>SQLITE_SECURE_DELETE</c> (default on): deleted rows are zeroed, so retention leaves no text behind.
    /// </summary>
    [Fact]
    public void SqliteStoreBase_DeletedRowLeavesNoTextInTheFile()
    {
        var path = Path.Combine(this.root, "probe.db");
        using (var store = new ProbeStore(path))
        {
            store.Run("CREATE TABLE t (x TEXT); INSERT INTO t VALUES ('zqresiduemarker'); DELETE FROM t;");
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(-1, File.ReadAllBytes(path).AsSpan().IndexOf("zqresiduemarker"u8));
    }

    private sealed class ProbeStore(string path) : SqliteStoreBase(path, enableWalMode: false)
    {
        public void Run(string sql) => this.ExecuteNonQuery(sql);
    }
}
