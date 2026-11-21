using System.Collections.Frozen;
using Cortex.Contained.Agent.Host.Tools;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Whether the recall tools are exposed: <c>History:Enabled</c>, unless switched off at runtime
/// (the behaviour gate's "tools off" arm, via the <c>SetHistoryRecallTools</c> hub method).
/// </summary>
public sealed class HistoryToolsSwitch
{
    private readonly IOptionsMonitor<HistoryOptions> options;
    private volatile bool forcedOff;

    public HistoryToolsSwitch(IOptionsMonitor<HistoryOptions> options)
    {
        this.options = options;
    }

    /// <summary>True when <c>history_search</c> and recall-mode <c>history_read</c> are available.</summary>
    public bool IsEnabled => this.options.CurrentValue.Enabled && !this.forcedOff;

    /// <summary>Runtime override; <c>false</c> hides the recall tools until set back to <c>true</c>.</summary>
    public void SetEnabled(bool enabled) => this.forcedOff = !enabled;
}

/// <summary>Hides <c>history_search</c> from the main agent while recall tools are off.</summary>
public sealed class HistoryToolsGate : IConversationToolGate
{
    private static readonly FrozenSet<string> hidden = FrozenSet.ToFrozenSet(["history_search"], StringComparer.OrdinalIgnoreCase);

    private readonly HistoryToolsSwitch toolsSwitch;

    public HistoryToolsGate(HistoryToolsSwitch toolsSwitch)
    {
        this.toolsSwitch = toolsSwitch;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetHiddenTools(string? conversationId) =>
        this.toolsSwitch.IsEnabled ? FrozenSet<string>.Empty : hidden;
}
