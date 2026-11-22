using System.Globalization;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>
/// The owner's persisted recall choices. A null member keeps the configured value
/// (<c>appsettings.json</c> / <c>History__*</c>, <c>Reranker__*</c>, <c>MemoryMcp__*</c> environment variables);
/// a set member overrides it at runtime and survives restarts and image upgrades (agent state volume).
/// </summary>
public sealed record RecallOverrides
{
    /// <summary>No overrides: the configured defaults apply.</summary>
    public static RecallOverrides None { get; } = new();

    public bool? HistoryEnabled { get; init; }

    public HistorySearchMode? HistorySearchMode { get; init; }

    public bool? HistoryDenseEnabled { get; init; }

    public MemorySearchMode? MemorySearchMode { get; init; }

    public int? RerankHistoryTopN { get; init; }

    public int? RerankHistoryMaxDocChars { get; init; }

    public int? RerankMemoryTopN { get; init; }

    public int? RerankMemoryMaxDocChars { get; init; }

    public int? RerankTimeoutMs { get; init; }

    /// <summary>camelCase names of the members that are set (reported as <c>overridden</c>).</summary>
    public IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        Add(this.HistoryEnabled.HasValue, "historyEnabled");
        Add(this.HistorySearchMode.HasValue, "historySearchMode");
        Add(this.HistoryDenseEnabled.HasValue, "historyDenseEnabled");
        Add(this.MemorySearchMode.HasValue, "memorySearchMode");
        Add(this.RerankHistoryTopN.HasValue, "rerankHistoryTopN");
        Add(this.RerankHistoryMaxDocChars.HasValue, "rerankHistoryMaxDocChars");
        Add(this.RerankMemoryTopN.HasValue, "rerankMemoryTopN");
        Add(this.RerankMemoryMaxDocChars.HasValue, "rerankMemoryMaxDocChars");
        Add(this.RerankTimeoutMs.HasValue, "rerankTimeoutMs");
        return names;

        void Add(bool set, string name)
        {
            if (set)
            {
                names.Add(name);
            }
        }
    }

    /// <summary>Overlays the set members of <paramref name="patch"/> on these overrides.</summary>
    public RecallOverrides With(RecallOverrides patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return new RecallOverrides
        {
            HistoryEnabled = patch.HistoryEnabled ?? this.HistoryEnabled,
            HistorySearchMode = patch.HistorySearchMode ?? this.HistorySearchMode,
            HistoryDenseEnabled = patch.HistoryDenseEnabled ?? this.HistoryDenseEnabled,
            MemorySearchMode = patch.MemorySearchMode ?? this.MemorySearchMode,
            RerankHistoryTopN = patch.RerankHistoryTopN ?? this.RerankHistoryTopN,
            RerankHistoryMaxDocChars = patch.RerankHistoryMaxDocChars ?? this.RerankHistoryMaxDocChars,
            RerankMemoryTopN = patch.RerankMemoryTopN ?? this.RerankMemoryTopN,
            RerankMemoryMaxDocChars = patch.RerankMemoryMaxDocChars ?? this.RerankMemoryMaxDocChars,
            RerankTimeoutMs = patch.RerankTimeoutMs ?? this.RerankTimeoutMs,
        };
    }
}

/// <summary>Parses and validates a <see cref="RecallSettingsPatchDto"/> (the same bounds the options enforce at startup).</summary>
public static class RecallSettingsValidation
{
    /// <summary>Smallest accepted <c>Reranker:TimeoutMs</c>.</summary>
    public const int MinTimeoutMs = 100;

    /// <summary>Largest accepted <c>Reranker:TimeoutMs</c>.</summary>
    public const int MaxTimeoutMs = 30_000;

    /// <summary>Converts <paramref name="patch"/> into overrides; returns the validation errors (empty when valid).</summary>
    public static IReadOnlyList<string> TryParse(RecallSettingsPatchDto patch, out RecallOverrides parsed)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var errors = new List<string>();
        HistorySearchMode? historyMode = null;
        if (patch.HistorySearchMode is { } h)
        {
            if (TryParseHistoryMode(h, out var mode))
            {
                historyMode = mode;
            }
            else
            {
                errors.Add("historySearchMode must be auto, hybrid or keyword.");
            }
        }

        MemorySearchMode? memoryMode = null;
        if (patch.MemorySearchMode is { } m)
        {
            if (TryParseMemoryMode(m, out var mode))
            {
                memoryMode = mode;
            }
            else
            {
                errors.Add("memorySearchMode must be dense, hybrid or keyword.");
            }
        }

        Range(patch.RerankHistoryTopN, RerankBudget.MinTopN, RerankBudget.MaxTopN, "rerankHistoryTopN");
        Range(patch.RerankMemoryTopN, RerankBudget.MinTopN, RerankBudget.MaxTopN, "rerankMemoryTopN");
        Range(patch.RerankHistoryMaxDocChars, RerankBudget.MinDocChars, RerankBudget.MaxDocCharsLimit, "rerankHistoryMaxDocChars");
        Range(patch.RerankMemoryMaxDocChars, RerankBudget.MinDocChars, RerankBudget.MaxDocCharsLimit, "rerankMemoryMaxDocChars");
        Range(patch.RerankTimeoutMs, MinTimeoutMs, MaxTimeoutMs, "rerankTimeoutMs");

        parsed = new RecallOverrides
        {
            HistoryEnabled = patch.HistoryEnabled,
            HistorySearchMode = historyMode,
            HistoryDenseEnabled = patch.HistoryDenseEnabled,
            MemorySearchMode = memoryMode,
            RerankHistoryTopN = patch.RerankHistoryTopN,
            RerankHistoryMaxDocChars = patch.RerankHistoryMaxDocChars,
            RerankMemoryTopN = patch.RerankMemoryTopN,
            RerankMemoryMaxDocChars = patch.RerankMemoryMaxDocChars,
            RerankTimeoutMs = patch.RerankTimeoutMs,
        };
        return errors;

        void Range(int? value, int min, int max, string name)
        {
            if (value is { } v && (v < min || v > max))
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be in [{min}, {max}]."));
            }
        }
    }

    /// <summary>
    /// Drops the members of <paramref name="overrides"/> that fail the validation bounds or hold an undefined
    /// enum value (a hand-edited or older file); returns the cleaned overrides and the names that were dropped.
    /// </summary>
    public static RecallOverrides Sanitize(RecallOverrides overrides, out IReadOnlyList<string> dropped)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var names = new List<string>();
        var clean = overrides with
        {
            HistorySearchMode = Defined(overrides.HistorySearchMode, "historySearchMode"),
            MemorySearchMode = Defined(overrides.MemorySearchMode, "memorySearchMode"),
            RerankHistoryTopN = InRange(overrides.RerankHistoryTopN, RerankBudget.MinTopN, RerankBudget.MaxTopN, "rerankHistoryTopN"),
            RerankMemoryTopN = InRange(overrides.RerankMemoryTopN, RerankBudget.MinTopN, RerankBudget.MaxTopN, "rerankMemoryTopN"),
            RerankHistoryMaxDocChars = InRange(overrides.RerankHistoryMaxDocChars, RerankBudget.MinDocChars, RerankBudget.MaxDocCharsLimit, "rerankHistoryMaxDocChars"),
            RerankMemoryMaxDocChars = InRange(overrides.RerankMemoryMaxDocChars, RerankBudget.MinDocChars, RerankBudget.MaxDocCharsLimit, "rerankMemoryMaxDocChars"),
            RerankTimeoutMs = InRange(overrides.RerankTimeoutMs, MinTimeoutMs, MaxTimeoutMs, "rerankTimeoutMs"),
        };
        dropped = names;
        return clean;

        TEnum? Defined<TEnum>(TEnum? value, string name)
            where TEnum : struct, Enum
        {
            if (value is { } v && !Enum.IsDefined(v))
            {
                names.Add(name);
                return null;
            }

            return value;
        }

        int? InRange(int? value, int min, int max, string name)
        {
            if (value is { } v && (v < min || v > max))
            {
                names.Add(name);
                return null;
            }

            return value;
        }
    }

    /// <summary>Parses <c>auto</c>, <c>hybrid</c> or <c>keyword</c> (case-insensitive).</summary>
    public static bool TryParseHistoryMode(string value, out HistorySearchMode mode)
    {
        mode = HistorySearchMode.Auto;
        return value is not null
            && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && Enum.TryParse(value, ignoreCase: true, out mode)
            && Enum.IsDefined(mode);
    }

    /// <summary>Parses <c>dense</c>, <c>hybrid</c> or <c>keyword</c> (case-insensitive).</summary>
    public static bool TryParseMemoryMode(string value, out MemorySearchMode mode)
    {
        mode = MemorySearchMode.Dense;
        return value is not null
            && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && Enum.TryParse(value, ignoreCase: true, out mode)
            && Enum.IsDefined(mode);
    }

    /// <summary>The lower-case wire form of a mode.</summary>
    public static string Text<TEnum>(TEnum mode)
        where TEnum : struct, Enum =>
        mode.ToString().ToLowerInvariant();
}
