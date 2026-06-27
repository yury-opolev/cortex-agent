using Cortex.Contained.Contracts.Config;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Mutable backing store for memory settings that can be updated at runtime.
/// Registered as a singleton and consumed by <see cref="IPostConfigureOptions{MemoryMcpOptions}"/>
/// and <see cref="IPostConfigureOptions{MemoryCompactionOptions}"/> so that
/// <see cref="IOptionsMonitor{T}.CurrentValue"/> reflects the latest values.
/// </summary>
/// <remarks>
/// This store intentionally does NOT use the file-backed <c>JsonFileSettingsStore&lt;T&gt;</c>
/// base. It holds in-memory volatile overrides and bridges runtime mutations into
/// <see cref="IOptionsMonitor{T}"/> via the Options post-configure + change-token pattern;
/// it has no JSON document on disk, so unifying it under the file-store base would be a
/// leaky abstraction.
/// </remarks>
public sealed class MemorySettingsStore : IDisposable
{
    private readonly object syncLock = new();
    private float? duplicateThreshold;
    private float? compactionSimilarityThreshold;
    private bool? compactionEnabled;
    private bool? idleCompactionEnabled;
    private int? idleResetMinutes;
    private int? imagePreserveRecentTurns;
    private bool? imageDescribeOnStrip;
    private int? compactionPreserveRecentTurns;
    private string? ollamaEndpoint;
    private string? ollamaApiKey;
    private bool? memoryEnabled;
    private CancellationTokenSource changeTokenSource = new();

    /// <summary>Override for <see cref="MemoryMcpOptions.DuplicateThreshold"/>.</summary>
    public float? DuplicateThreshold
    {
        get { lock (this.syncLock) return this.duplicateThreshold; }
    }

    /// <summary>Override for <see cref="MemoryCompactionOptions.SimilarityThreshold"/>.</summary>
    public float? CompactionSimilarityThreshold
    {
        get { lock (this.syncLock) return this.compactionSimilarityThreshold; }
    }

    /// <summary>Override for <see cref="MemoryCompactionOptions.Enabled"/>.</summary>
    public bool? CompactionEnabled
    {
        get { lock (this.syncLock) return this.compactionEnabled; }
    }

    /// <summary>Override for idle compaction behavior (true = summarize, false = wipe).</summary>
    public bool? IdleCompactionEnabled
    {
        get { lock (this.syncLock) return this.idleCompactionEnabled; }
    }

    /// <summary>Override for idle reset timeout in minutes (0 = disabled).</summary>
    public int? IdleResetMinutes
    {
        get { lock (this.syncLock) return this.idleResetMinutes; }
    }

    /// <summary>Override for <see cref="ImageAgingConfig.PreserveRecentTurns"/>.</summary>
    public int? ImagePreserveRecentTurns
    {
        get { lock (this.syncLock) return this.imagePreserveRecentTurns; }
    }

    /// <summary>Override for <see cref="ImageAgingConfig.DescribeOnStrip"/>.</summary>
    public bool? ImageDescribeOnStrip
    {
        get { lock (this.syncLock) return this.imageDescribeOnStrip; }
    }

    /// <summary>Override for <see cref="ConversationCompactionConfig.PreserveRecentTurns"/>.</summary>
    public int? CompactionPreserveRecentTurns
    {
        get { lock (this.syncLock) return this.compactionPreserveRecentTurns; }
    }

    /// <summary>Override for <see cref="OllamaOptions.Endpoint"/>. Null = leave as configured.</summary>
    public string? OllamaEndpoint
    {
        get { lock (this.syncLock) return this.ollamaEndpoint; }
    }

    /// <summary>
    /// Override for <see cref="OllamaOptions.ApiKey"/>. Null = not provided (leave as-is).
    /// Empty string = explicitly clear the key (set to null in options).
    /// </summary>
    public string? OllamaApiKey
    {
        get { lock (this.syncLock) return this.ollamaApiKey; }
    }

    /// <summary>Master built-in-memory switch. Null = never pushed (treated as enabled).</summary>
    public bool? MemoryEnabled
    {
        get { lock (this.syncLock) return this.memoryEnabled; }
    }

    /// <summary>Effective enablement: true unless explicitly pushed false.</summary>
    public bool IsMemoryEnabled
    {
        get { lock (this.syncLock) return this.memoryEnabled ?? true; }
    }

    /// <summary>
    /// Update all settings and signal that options should be reloaded.
    /// </summary>
    public void Update(
        float? duplicateThreshold,
        float? compactionSimilarityThreshold,
        bool? compactionEnabled,
        bool? idleCompactionEnabled = null,
        int? idleResetMinutes = null,
        int? imagePreserveRecentTurns = null,
        bool? imageDescribeOnStrip = null,
        int? compactionPreserveRecentTurns = null,
        string? ollamaEndpoint = null,
        string? ollamaApiKey = null,
        bool? memoryEnabled = null)
    {
        CancellationTokenSource oldCts;
        lock (this.syncLock)
        {
            this.duplicateThreshold = duplicateThreshold;
            this.compactionSimilarityThreshold = compactionSimilarityThreshold;
            this.compactionEnabled = compactionEnabled;
            this.idleCompactionEnabled = idleCompactionEnabled;
            this.idleResetMinutes = idleResetMinutes;
            this.imagePreserveRecentTurns = imagePreserveRecentTurns;
            this.imageDescribeOnStrip = imageDescribeOnStrip;
            this.compactionPreserveRecentTurns = compactionPreserveRecentTurns;
            this.ollamaEndpoint = ollamaEndpoint;
            this.ollamaApiKey = ollamaApiKey;
            this.memoryEnabled = memoryEnabled;

            // Swap the CTS so the old change token fires and a new one is ready
            oldCts = this.changeTokenSource;
            this.changeTokenSource = new CancellationTokenSource();
        }

        // Signal the change outside the lock
        oldCts.Cancel();
        oldCts.Dispose();
    }

    /// <summary>
    /// Get a change token that fires when settings are updated.
    /// Used by <see cref="MemorySettingsChangeTokenSource{T}"/>.
    /// </summary>
    internal IChangeToken GetChangeToken()
    {
        lock (this.syncLock)
        {
            return new CancellationChangeToken(this.changeTokenSource.Token);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.changeTokenSource.Dispose();
    }
}

/// <summary>
/// Provides an <see cref="IOptionsChangeTokenSource{T}"/> backed by
/// <see cref="MemorySettingsStore"/>, so <see cref="IOptionsMonitor{T}"/>
/// invalidates its cache whenever runtime settings are updated.
/// </summary>
public sealed class MemorySettingsChangeTokenSource<T>(MemorySettingsStore store)
    : IOptionsChangeTokenSource<T>
{
    public string Name => Options.DefaultName;
    public IChangeToken GetChangeToken() => store.GetChangeToken();
}

/// <summary>
/// Applies runtime overrides from <see cref="MemorySettingsStore"/> to <see cref="MemoryMcpOptions"/>
/// after configuration binding, so <see cref="IOptionsMonitor{T}.CurrentValue"/> is always current.
/// </summary>
public sealed class MemoryMcpPostConfigure(MemorySettingsStore store) : IPostConfigureOptions<MemoryMcpOptions>
{
    public void PostConfigure(string? name, MemoryMcpOptions options)
    {
        if (store.DuplicateThreshold.HasValue)
        {
            options.DuplicateThreshold = store.DuplicateThreshold.Value;
        }

        if (!string.IsNullOrWhiteSpace(store.OllamaEndpoint))
        {
            options.Ollama.Endpoint = store.OllamaEndpoint;
        }

        // null = not provided → leave options.Ollama.ApiKey as bound from config
        // empty string = explicitly clear the key
        // non-empty string = set the key
        if (store.OllamaApiKey is not null)
        {
            options.Ollama.ApiKey = string.IsNullOrEmpty(store.OllamaApiKey) ? null : store.OllamaApiKey;
        }
    }
}

/// <summary>
/// Applies runtime overrides from <see cref="MemorySettingsStore"/> to <see cref="MemoryCompactionOptions"/>
/// after configuration binding.
/// </summary>
public sealed class MemoryCompactionPostConfigure(MemorySettingsStore store) : IPostConfigureOptions<MemoryCompactionOptions>
{
    public void PostConfigure(string? name, MemoryCompactionOptions options)
    {
        if (store.CompactionSimilarityThreshold.HasValue)
        {
            options.SimilarityThreshold = store.CompactionSimilarityThreshold.Value;
        }

        if (store.CompactionEnabled.HasValue)
        {
            options.Enabled = store.CompactionEnabled.Value;
        }
    }
}

/// <summary>
/// Applies runtime overrides from <see cref="MemorySettingsStore"/> to <see cref="ConversationCompactionConfig"/>
/// after configuration binding.
/// </summary>
public sealed class ConversationCompactionPostConfigure(MemorySettingsStore store) : IPostConfigureOptions<ConversationCompactionConfig>
{
    public void PostConfigure(string? name, ConversationCompactionConfig options)
    {
        if (store.CompactionPreserveRecentTurns.HasValue)
        {
            options.PreserveRecentTurns = store.CompactionPreserveRecentTurns.Value;
        }
    }
}

/// <summary>
/// Applies runtime overrides from <see cref="MemorySettingsStore"/> to <see cref="ImageAgingConfig"/>
/// after configuration binding.
/// </summary>
public sealed class ImageAgingPostConfigure(MemorySettingsStore store) : IPostConfigureOptions<ImageAgingConfig>
{
    public void PostConfigure(string? name, ImageAgingConfig options)
    {
        if (store.ImagePreserveRecentTurns.HasValue)
        {
            options.PreserveRecentTurns = store.ImagePreserveRecentTurns.Value;
        }

        if (store.ImageDescribeOnStrip.HasValue)
        {
            options.DescribeOnStrip = store.ImageDescribeOnStrip.Value;
        }
    }
}
