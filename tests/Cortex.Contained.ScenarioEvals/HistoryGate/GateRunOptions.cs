using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.ScenarioEvals.HistoryGate;

/// <summary>
/// Behaviour-gate run settings, bound from <c>ScenarioEval:HistoryGate</c>. <see cref="SearchMode"/>
/// labels the run: <c>keyword</c> (increment 1) or <c>hybrid</c> (increment 3, staging with
/// <c>History:Dense:Enabled=true</c> and <c>History:Dense:OpenSegmentDebounceSeconds=0</c>, so the
/// imported fixture is embedded within <see cref="EmbeddingSettle"/>).
/// </summary>
public sealed record GateRunOptions(int Samples, TimeSpan Settle, string SearchMode, TimeSpan EmbeddingSettle)
{
    /// <summary>The wait after import, reset and tool switch before the prompt is sent.</summary>
    public TimeSpan TotalSettle => this.SearchMode == "hybrid" ? this.Settle + this.EmbeddingSettle : this.Settle;

    /// <summary>Reads the section; missing values fall back to 3 samples, 5 s settle, keyword mode and 20 s embedding settle.</summary>
    public static GateRunOptions From(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var mode = (section["SearchMode"] ?? "keyword").Trim().ToLowerInvariant();
        if (mode is not ("keyword" or "hybrid"))
        {
            throw new InvalidOperationException("ScenarioEval:HistoryGate:SearchMode must be 'keyword' or 'hybrid'.");
        }

        return new GateRunOptions(
            int.TryParse(section["Samples"], CultureInfo.InvariantCulture, out var samples) ? samples : 3,
            TimeSpan.FromSeconds(double.TryParse(section["SettleSeconds"], CultureInfo.InvariantCulture, out var settle) ? settle : 5),
            mode,
            TimeSpan.FromSeconds(double.TryParse(section["EmbeddingSettleSeconds"], CultureInfo.InvariantCulture, out var embed) ? embed : 20));
    }
}
