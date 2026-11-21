namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Configuration for the keyword history locator, bound from the <c>History</c> section
/// (<c>appsettings.json</c>, <c>cortex.yml</c> or <c>History__*</c> environment variables).
/// Values that change segmentation are part of <see cref="HistoryConfigFingerprint"/>, so
/// changing them triggers an index rebuild.
/// </summary>
public sealed class HistoryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "History";

    /// <summary>Master switch: runs the projector and exposes the recall tools. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>IANA time zone used for local dates, segment cuts and displayed times.</summary>
    public string TimeZone { get; set; } = "Europe/Copenhagen";

    /// <summary>A gap of at least this many minutes between two messages starts a new segment.</summary>
    public int IdleGapMinutes { get; set; } = 30;

    /// <summary>A segment is closed before its word (FTS token) count would pass this value.</summary>
    public int MaxSegmentWords { get; set; } = 1500;

    /// <summary>Relevance queries drop unquoted terms whose segment document frequency exceeds this ratio.</summary>
    public double DfCapRatio { get; set; } = 0.2;

    /// <summary>Aggregate token cap for one recall-tool response.</summary>
    public int SearchTokenCap { get; set; } = 1500;

    /// <summary>Maximum source rows matched directly from the journal tail the index has not applied yet.</summary>
    public int UnindexedTailMax { get; set; } = 2000;

    /// <summary>Messages per bounded batch during an initial build or rebuild.</summary>
    public int BuildBatchMessages { get; set; } = 2000;

    /// <summary>Hours added after the last purged message when quarantining curated memories (D10).</summary>
    public int QuarantineExtractionSlackHours { get; set; } = 24;

    /// <summary>Purged messages further apart than this many hours form separate quarantine windows.</summary>
    public int QuarantineClusterGapHours { get; set; } = 6;

    /// <summary>Days between maintenance <c>VACUUM</c> runs of <c>messages.db</c> and the current index.</summary>
    public int VacuumIntervalDays { get; set; } = 7;

    /// <summary>A purge that deletes more rows than this schedules a <c>VACUUM</c> immediately.</summary>
    public int VacuumAfterPurgeRows { get; set; } = 1000;

    /// <summary>Local smoke-evaluation artifact root; overlapping runs are destroyed on purge.</summary>
    public string SmokeArtifactsDirectory { get; set; } = "/app/state/eval/smoke";

    /// <summary>Keyword index variant; part of the index fingerprint (§4.2 stemming ablation).</summary>
    public HistoryKeywordVariant KeywordVariant { get; set; } = HistoryKeywordVariant.Plain;

    /// <summary>Dense retrieval settings (<c>History:Dense</c>).</summary>
    public HistoryDenseOptions Dense { get; set; } = new();

    /// <summary>Hybrid ranking settings (<c>History:Ranking</c>).</summary>
    public HistoryRankingOptions Ranking { get; set; } = new();

    /// <summary>Resolves <see cref="TimeZone"/>.</summary>
    public TimeZoneInfo ResolveTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(this.TimeZone);

    /// <summary>Returns human-readable validation errors; empty when the options are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        try
        {
            _ = this.ResolveTimeZone();
        }
        catch (TimeZoneNotFoundException)
        {
            errors.Add($"History:TimeZone '{this.TimeZone}' is not a known time zone.");
        }

        if (this.IdleGapMinutes < 1)
        {
            errors.Add("History:IdleGapMinutes must be at least 1.");
        }

        if (this.MaxSegmentWords < 50)
        {
            errors.Add("History:MaxSegmentWords must be at least 50.");
        }

        if (this.DfCapRatio <= 0 || this.DfCapRatio > 1)
        {
            errors.Add("History:DfCapRatio must be in (0, 1].");
        }

        if (this.SearchTokenCap < 200)
        {
            errors.Add("History:SearchTokenCap must be at least 200.");
        }

        if (this.UnindexedTailMax < 0)
        {
            errors.Add("History:UnindexedTailMax must not be negative.");
        }

        if (this.BuildBatchMessages < 100)
        {
            errors.Add("History:BuildBatchMessages must be at least 100.");
        }

        errors.AddRange(this.Dense.Validate());
        errors.AddRange(this.Ranking.Validate());
        return errors;
    }
}
