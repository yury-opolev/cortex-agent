using Cortex.Contained.Launcher.Services;

namespace Cortex.Contained.Launcher.Tests.Services;

public sealed class ComposeFileSyncTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"compose-sync-{Guid.NewGuid():N}");
    private readonly string bundle;
    private readonly string dataDirectory;

    public ComposeFileSyncTests()
    {
        this.bundle = Path.Combine(this.root, "bundle");
        this.dataDirectory = Path.Combine(this.root, "data", "Cortex");
        Directory.CreateDirectory(this.bundle);
    }

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    [Fact]
    public void Refresh_BundledComposeIsNewer_ReplacesTheStableCopy()
    {
        Directory.CreateDirectory(this.dataDirectory);
        File.WriteAllText(Path.Combine(this.dataDirectory, "docker-compose.yml"), "services: { embeddings: {} }");
        File.WriteAllText(Path.Combine(this.bundle, "docker-compose.yml"), "services: { embeddings: {}, reranker: {} }");

        var path = ComposeFileSync.Refresh(this.bundle, this.dataDirectory);

        Assert.Equal(Path.Combine(this.dataDirectory, "docker-compose.yml"), path);
        Assert.Contains("reranker", File.ReadAllText(path!), StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_NoStableCopyYet_CreatesTheDataDirectoryAndCopy()
    {
        File.WriteAllText(Path.Combine(this.bundle, "docker-compose.yml"), "services: {}");

        var path = ComposeFileSync.Refresh(this.bundle, this.dataDirectory);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Refresh_BundledGpuOverride_IsCopiedNextToTheStableCompose()
    {
        File.WriteAllText(Path.Combine(this.bundle, "docker-compose.yml"), "services: {}");
        File.WriteAllText(Path.Combine(this.bundle, "docker-compose.embeddings-gpu.yml"), "services: { embeddings: { deploy: {} } }");

        ComposeFileSync.Refresh(this.bundle, this.dataDirectory);

        var copied = Path.Combine(this.dataDirectory, "docker-compose.embeddings-gpu.yml");
        Assert.Contains("deploy", File.ReadAllText(copied), StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_BundledGpuOverrideIsNewer_ReplacesTheStableCopy()
    {
        Directory.CreateDirectory(this.dataDirectory);
        var stable = Path.Combine(this.dataDirectory, "docker-compose.embeddings-gpu.yml");
        File.WriteAllText(stable, "old");
        File.WriteAllText(Path.Combine(this.bundle, "docker-compose.yml"), "services: {}");
        File.WriteAllText(Path.Combine(this.bundle, "docker-compose.embeddings-gpu.yml"), "new");

        ComposeFileSync.Refresh(this.bundle, this.dataDirectory);

        Assert.Equal("new", File.ReadAllText(stable));
    }

    [Fact]
    public void Refresh_NoBundledGpuOverride_StillSyncsTheComposeFile()
    {
        File.WriteAllText(Path.Combine(this.bundle, "docker-compose.yml"), "services: {}");

        var path = ComposeFileSync.Refresh(this.bundle, this.dataDirectory);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(this.dataDirectory, "docker-compose.embeddings-gpu.yml")));
    }

    [Fact]
    public void Refresh_BundledHostOverride_IsCopiedNextToTheStableCompose()
    {
        File.WriteAllText(Path.Combine(this.bundle, "docker-compose.yml"), "services: {}");
        File.WriteAllText(Path.Combine(this.bundle, "docker-compose.embeddings-host.yml"), "services: { embeddings: { ports: [] } }");

        ComposeFileSync.Refresh(this.bundle, this.dataDirectory);

        var copied = Path.Combine(this.dataDirectory, "docker-compose.embeddings-host.yml");
        Assert.Contains("ports", File.ReadAllText(copied), StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_NoBundledCompose_ReturnsNullAndKeepsTheStableCopy()
    {
        Directory.CreateDirectory(this.dataDirectory);
        var stable = Path.Combine(this.dataDirectory, "docker-compose.yml");
        File.WriteAllText(stable, "kept");

        Assert.Null(ComposeFileSync.Refresh(this.bundle, this.dataDirectory));
        Assert.Equal("kept", File.ReadAllText(stable));
    }
}
