using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Config;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class MemorySettingsStoreImageAgingTests
{
    [Fact]
    public void ImageAgingOverrides_DefaultNull()
    {
        var store = new MemorySettingsStore();

        Assert.Null(store.ImagePreserveRecentTurns);
        Assert.Null(store.ImageDescribeOnStrip);
    }

    [Fact]
    public void Update_SetsImageAgingOverrides()
    {
        var store = new MemorySettingsStore();

        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            idleCompactionEnabled: null,
            idleResetMinutes: null,
            imagePreserveRecentTurns: 5,
            imageDescribeOnStrip: false);

        Assert.Equal(5, store.ImagePreserveRecentTurns);
        Assert.Equal(false, store.ImageDescribeOnStrip);
    }

    [Fact]
    public void ImageAgingPostConfigure_AppliesOverrides()
    {
        var store = new MemorySettingsStore();
        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            idleCompactionEnabled: null,
            idleResetMinutes: null,
            imagePreserveRecentTurns: 3,
            imageDescribeOnStrip: false);

        var opts = new ImageAgingConfig();
        new ImageAgingPostConfigure(store).PostConfigure(null, opts);

        Assert.Equal(3, opts.PreserveRecentTurns);
        Assert.False(opts.DescribeOnStrip);
    }

    [Fact]
    public void ImageAgingPostConfigure_NoOverrides_LeavesDefaults()
    {
        var store = new MemorySettingsStore();

        var opts = new ImageAgingConfig();
        new ImageAgingPostConfigure(store).PostConfigure(null, opts);

        Assert.Equal(4, opts.PreserveRecentTurns);
        Assert.True(opts.DescribeOnStrip);
    }
}

public sealed class MemorySettingsStoreConversationCompactionTests
{
    [Fact]
    public void CompactionPreserveRecentTurns_DefaultNull()
    {
        var store = new MemorySettingsStore();

        Assert.Null(store.CompactionPreserveRecentTurns);
    }

    [Fact]
    public void Update_SetsCompactionPreserveRecentTurns()
    {
        var store = new MemorySettingsStore();

        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            idleCompactionEnabled: null,
            idleResetMinutes: null,
            imagePreserveRecentTurns: null,
            imageDescribeOnStrip: null,
            compactionPreserveRecentTurns: 7);

        Assert.Equal(7, store.CompactionPreserveRecentTurns);
    }

    [Fact]
    public void ConversationCompactionPostConfigure_AppliesOverride()
    {
        var store = new MemorySettingsStore();
        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            idleCompactionEnabled: null,
            idleResetMinutes: null,
            imagePreserveRecentTurns: null,
            imageDescribeOnStrip: null,
            compactionPreserveRecentTurns: 2);

        var opts = new ConversationCompactionConfig();
        new ConversationCompactionPostConfigure(store).PostConfigure(null, opts);

        Assert.Equal(2, opts.PreserveRecentTurns);
    }

    [Fact]
    public void ConversationCompactionPostConfigure_NoOverride_LeavesDefault()
    {
        var store = new MemorySettingsStore();

        var opts = new ConversationCompactionConfig();
        new ConversationCompactionPostConfigure(store).PostConfigure(null, opts);

        Assert.Equal(4, opts.PreserveRecentTurns);
        Assert.Equal(0.25, opts.PreserveBudgetRatio);
    }
}

public sealed class MemorySettingsStoreOllamaTests
{
    [Fact]
    public void OllamaFields_DefaultNull()
    {
        var store = new MemorySettingsStore();

        Assert.Null(store.OllamaEndpoint);
        Assert.Null(store.OllamaApiKey);
    }

    [Fact]
    public void Update_SetsOllamaEndpointAndKey()
    {
        var store = new MemorySettingsStore();

        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            ollamaEndpoint: "http://mac:11434",
            ollamaApiKey: "k");

        Assert.Equal("http://mac:11434", store.OllamaEndpoint);
        Assert.Equal("k", store.OllamaApiKey);
    }

    [Fact]
    public void MemoryMcpPostConfigure_AppliesOllamaEndpointAndKey()
    {
        var store = new MemorySettingsStore();
        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            ollamaEndpoint: "http://mac:11434",
            ollamaApiKey: "k");

        var opts = new MemoryMcpOptions();
        new MemoryMcpPostConfigure(store).PostConfigure(null, opts);

        Assert.Equal("http://mac:11434", opts.Ollama.Endpoint);
        Assert.Equal("k", opts.Ollama.ApiKey);
    }

    [Fact]
    public void MemoryMcpPostConfigure_EmptyStringKey_ClearsApiKey()
    {
        var store = new MemorySettingsStore();
        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            ollamaEndpoint: "http://mac:11434",
            ollamaApiKey: "");

        // Pre-set a key value in options to verify it is cleared
        var opts = new MemoryMcpOptions();
        opts.Ollama.ApiKey = "old-key";
        new MemoryMcpPostConfigure(store).PostConfigure(null, opts);

        Assert.Null(opts.Ollama.ApiKey);
    }

    [Fact]
    public void MemoryMcpPostConfigure_NullKey_LeavesExistingApiKey()
    {
        var store = new MemorySettingsStore();
        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            ollamaEndpoint: null,
            ollamaApiKey: null);

        // Key from bound config should be untouched
        var opts = new MemoryMcpOptions();
        opts.Ollama.ApiKey = "bound-key";
        new MemoryMcpPostConfigure(store).PostConfigure(null, opts);

        Assert.Equal("bound-key", opts.Ollama.ApiKey);
    }

    [Fact]
    public void MemoryMcpPostConfigure_NullEndpoint_LeavesExistingEndpoint()
    {
        var store = new MemorySettingsStore();
        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            ollamaEndpoint: null,
            ollamaApiKey: null);

        var opts = new MemoryMcpOptions();
        opts.Ollama.Endpoint = "http://original:11434";
        new MemoryMcpPostConfigure(store).PostConfigure(null, opts);

        Assert.Equal("http://original:11434", opts.Ollama.Endpoint);
    }

    [Fact]
    public void Update_OllamaFields_FiresChangeToken()
    {
        var store = new MemorySettingsStore();
        var token = store.GetChangeToken();
        Assert.False(token.HasChanged);

        store.Update(
            duplicateThreshold: null,
            compactionSimilarityThreshold: null,
            compactionEnabled: null,
            ollamaEndpoint: "http://mac:11434",
            ollamaApiKey: "k");

        Assert.True(token.HasChanged);
    }
}
