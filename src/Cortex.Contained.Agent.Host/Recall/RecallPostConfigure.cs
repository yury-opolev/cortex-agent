using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Reranking;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>Invalidates <c>IOptionsMonitor&lt;T&gt;</c> whenever <see cref="RecallSettingsStore"/> applies an update.</summary>
public sealed class RecallSettingsChangeTokenSource<T>(RecallSettingsStore store) : IOptionsChangeTokenSource<T>
{
    /// <inheritdoc />
    public string Name => Options.DefaultName;

    /// <inheritdoc />
    public IChangeToken GetChangeToken() => store.GetChangeToken();
}

/// <summary>Applies the recall overrides to <see cref="HistoryOptions"/> (<c>Enabled</c>, <c>DefaultSearchMode</c>, <c>Dense:Enabled</c>).</summary>
public sealed class RecallHistoryPostConfigure(RecallSettingsStore store) : IPostConfigureOptions<HistoryOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, HistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var overrides = store.Current;
        options.Enabled = overrides.HistoryEnabled ?? options.Enabled;
        options.DefaultSearchMode = overrides.HistorySearchMode ?? options.DefaultSearchMode;
        options.Dense.Enabled = overrides.HistoryDenseEnabled ?? options.Dense.Enabled;
    }
}

/// <summary>Applies the recall overrides to the agent-side <see cref="RerankerOptions"/> budgets and timeout.</summary>
public sealed class RecallRerankerPostConfigure(RecallSettingsStore store) : IPostConfigureOptions<RerankerOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, RerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var overrides = store.Current;
        options.History.TopN = overrides.RerankHistoryTopN ?? options.History.TopN;
        options.History.MaxDocChars = overrides.RerankHistoryMaxDocChars ?? options.History.MaxDocChars;
        options.Memory.TopN = overrides.RerankMemoryTopN ?? options.Memory.TopN;
        options.Memory.MaxDocChars = overrides.RerankMemoryMaxDocChars ?? options.Memory.MaxDocChars;
        options.TimeoutMs = overrides.RerankTimeoutMs ?? options.TimeoutMs;
    }
}

/// <summary>Applies the recall override of <c>MemoryMcp:Search:DefaultMode</c>.</summary>
public sealed class RecallMemorySearchPostConfigure(RecallSettingsStore store) : IPostConfigureOptions<MemoryMcpOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, MemoryMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Search.DefaultMode = store.Current.MemorySearchMode ?? options.Search.DefaultMode;
    }
}

/// <summary>Registers the recall settings store, its post-configures and change-token sources.</summary>
public static class RecallSettingsRegistration
{
    /// <summary>File name of the overrides in the agent state directory.</summary>
    public const string FileName = "recall-settings.json";

    /// <summary>Adds <see cref="RecallSettingsStore"/> at <c>&lt;stateRoot&gt;/recall-settings.json</c> and wires it into the options pipeline.</summary>
    public static IServiceCollection AddRecallSettings(this IServiceCollection services, string stateRoot)
    {
        services.AddSingleton(sp => new RecallSettingsStore(
            Path.Combine(stateRoot, FileName),
            sp.GetRequiredService<ILogger<RecallSettingsStore>>()));
        services.AddSingleton<IPostConfigureOptions<HistoryOptions>, RecallHistoryPostConfigure>();
        services.AddSingleton<IPostConfigureOptions<RerankerOptions>, RecallRerankerPostConfigure>();
        services.AddSingleton<IPostConfigureOptions<MemoryMcpOptions>, RecallMemorySearchPostConfigure>();
        services.AddSingleton<IOptionsChangeTokenSource<HistoryOptions>, RecallSettingsChangeTokenSource<HistoryOptions>>();
        services.AddSingleton<IOptionsChangeTokenSource<RerankerOptions>, RecallSettingsChangeTokenSource<RerankerOptions>>();
        services.AddSingleton<IOptionsChangeTokenSource<MemoryMcpOptions>, RecallSettingsChangeTokenSource<MemoryMcpOptions>>();
        return services;
    }
}
