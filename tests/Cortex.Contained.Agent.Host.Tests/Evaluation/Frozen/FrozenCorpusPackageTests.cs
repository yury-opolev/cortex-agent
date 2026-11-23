using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

/// <summary>Export from an encrypted install, import elsewhere (here: a plaintext install), hashes verified.</summary>
public sealed class FrozenCorpusPackageTests : IAsyncDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"package-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private async Task<(FrozenCorpusStore Store, FrozenCorpusManifest Manifest)> FreezeAsync()
    {
        var state = Path.Combine(this.root, "state");
        var sources = await FrozenCorpusBuilderTests.SeedAsync(state, EvalTestKeys.Ring(), this.protection);
        var store = new FrozenCorpusStore(Path.Combine(state, "eval"));
        var result = await FrozenCorpusBuilder.FreezeAsync(sources, store, this.protection, TimeProvider.System, CancellationToken.None);
        return (store, result.Manifest!);
    }

    [Fact]
    public async Task ExportThenImportElsewhere_SameSanitisedCorpusDatasetsAndRegistration()
    {
        var (store, manifest) = await this.FreezeAsync();

        var package = FrozenCorpusPackage.Export(store, manifest.Version, this.protection, Passphrase, TimeProvider.System);

        Assert.Equal("cortex-eval-corpus-v1.cxpkg", package.FileName);
        Assert.Equal(package.Sha256, store.Read("v1")!.PackageSha256);
        Assert.DoesNotContain("Person", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(package.Path)), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(store.PackagesDirectory, "*.tmp"));

        var elsewhere = new FrozenCorpusStore(Path.Combine(this.root, "elsewhere", "eval"));
        var imported = FrozenCorpusPackage.Import(elsewhere, package.Path, EvalProtection.Plaintext, Passphrase);

        Assert.True(imported.Imported);
        var directory = elsewhere.DirectoryOf("v1");
        Assert.Equal(manifest.Files[FrozenCorpusBuilder.MessagesDigestKey], FrozenCorpusBuilder.MessagesDigest(Path.Combine(directory, "messages.db"), null));
        var history = new EvalWorkspace(Path.Combine(directory, "history"));
        history.VerifyRegistration();
        var original = new EvalWorkspace(Path.Combine(store.DirectoryOf("v1"), "history"), this.protection).ReadDataset();
        Assert.Equal(original.Select(EvalDataset.ToLine), history.ReadDataset().Select(EvalDataset.ToLine));
        Assert.False(File.Exists(Path.Combine(directory, FrozenCorpusBuilder.SecretFileName)));
    }

    [Fact]
    public async Task Import_WrongPassphraseOrExistingVersion_RefusedAndNothingLeftBehind()
    {
        var (store, manifest) = await this.FreezeAsync();
        var package = FrozenCorpusPackage.Export(store, manifest.Version, this.protection, Passphrase, TimeProvider.System);
        var elsewhere = new FrozenCorpusStore(Path.Combine(this.root, "elsewhere", "eval"));

        Assert.Throws<InvalidDataException>(() => FrozenCorpusPackage.Import(elsewhere, package.Path, EvalProtection.Plaintext, "another passphrase"));
        Assert.Empty(Directory.EnumerateDirectories(elsewhere.Root));
        Assert.Throws<InvalidOperationException>(() => FrozenCorpusPackage.Import(store, package.Path, this.protection, Passphrase));
    }
}
