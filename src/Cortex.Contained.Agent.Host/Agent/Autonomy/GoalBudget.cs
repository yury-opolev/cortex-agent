using System.Globalization;

namespace Cortex.Contained.Agent.Host.Agent.Autonomy;

/// <summary>
/// Persistable backstop for an autonomous subagent run.
/// <para>
/// The budget is tracked as consumed time plus consumed continuations so a recovered process can
/// resume the same run without silently resetting the clock.
/// </para>
/// </summary>
internal sealed class GoalBudget
{
    public static readonly TimeSpan DefaultWallClockLimit = TimeSpan.FromDays(7);

    public const int DefaultContinuationLimit = 10_000;

    private readonly TimeProvider clock;
    private readonly DateTimeOffset resumedAt;
    private readonly TimeSpan restoredElapsed;
    private int continuationsUsed;

    private GoalBudget(
        TimeProvider clock,
        TimeSpan? wallClockLimit,
        int? continuationLimit,
        GoalBudgetConsumed consumed)
    {
        ArgumentNullException.ThrowIfNull(clock);

        ValidateLimit(wallClockLimit, continuationLimit);

        this.clock = clock;
        this.WallClockLimit = wallClockLimit;
        this.ContinuationLimit = continuationLimit;
        this.restoredElapsed = consumed.Elapsed;
        this.continuationsUsed = consumed.ContinuationsUsed;
        this.resumedAt = clock.GetUtcNow();
    }

    public TimeSpan? WallClockLimit { get; }

    public int? ContinuationLimit { get; }

    public GoalBudgetConsumed Consumed =>
        new(this.restoredElapsed + (this.clock.GetUtcNow() - this.resumedAt), this.continuationsUsed);

    public TimeSpan? WallClockRemaining => Remaining(this.WallClockLimit, this.Consumed.Elapsed);

    public int? ContinuationsRemaining => this.ContinuationLimit is { } limit
        ? Math.Max(0, limit - this.Consumed.ContinuationsUsed)
        : null;

    public bool IsExhausted =>
        this.WallClockRemaining == TimeSpan.Zero || this.ContinuationsRemaining == 0;

    public static GoalBudget Create(TimeProvider clock) =>
        Create(clock, DefaultWallClockLimit, DefaultContinuationLimit);

    public static GoalBudget Create(
        TimeProvider clock,
        TimeSpan? wallClockLimit,
        int? continuationLimit) =>
        new(
            clock,
            wallClockLimit,
            continuationLimit,
            new GoalBudgetConsumed(TimeSpan.Zero, 0));

    public static GoalBudget Rehydrate(
        GoalBudgetConsumed consumed,
        TimeProvider clock) =>
        Rehydrate(consumed, clock, DefaultWallClockLimit, DefaultContinuationLimit);

    public static GoalBudget Rehydrate(
        GoalBudgetConsumed consumed,
        TimeProvider clock,
        TimeSpan? wallClockLimit,
        int? continuationLimit) =>
        new(
            clock,
            wallClockLimit,
            continuationLimit,
            consumed);

    public void RecordContinuation()
    {
        this.continuationsUsed++;
    }

    public static TimeSpan? ParseWallClockLimit(string text)
    {
        if (IsNoLimit(text))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
        {
            throw new FormatException("Wall-clock budget must be a duration like 90s, 30m, 2h, 7d, or none.");
        }

        var trimmed = text.Trim();
        var unit = trimmed[^1];
        var numberText = trimmed[..^1];

        if (!long.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0)
        {
            throw new FormatException("Wall-clock budget duration must have a positive whole-number amount.");
        }

        return unit switch
        {
            's' or 'S' => TimeSpan.FromSeconds(amount),
            'm' or 'M' => TimeSpan.FromMinutes(amount),
            'h' or 'H' => TimeSpan.FromHours(amount),
            'd' or 'D' => TimeSpan.FromDays(amount),
            _ => throw new FormatException("Wall-clock budget unit must be one of s, m, h, or d."),
        };
    }

    public static int? ParseContinuationLimit(string text)
    {
        if (IsNoLimit(text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || limit <= 0)
        {
            throw new FormatException("Continuation budget must be a positive whole number or none.");
        }

        return limit;
    }

    private static TimeSpan? Remaining(TimeSpan? limit, TimeSpan consumed)
    {
        if (limit is not { } finiteLimit)
        {
            return null;
        }

        var remaining = finiteLimit - consumed;
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    private static void ValidateLimit(TimeSpan? wallClockLimit, int? continuationLimit)
    {
        // Any POSITIVE limit is allowed, in either direction from the default. An earlier version
        // permitted only upward overrides, which inverted the safe default: an operator could not
        // express "cap this run at an hour", but could trivially express "no cap at all" — for a
        // subsystem that runs ungated shell and file tools unattended for days.
        if (wallClockLimit is { } finiteWallClockLimit && finiteWallClockLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wallClockLimit),
                "An autonomous goal wall-clock budget must be positive.");
        }

        if (continuationLimit is { } finiteContinuationLimit && finiteContinuationLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(continuationLimit),
                "An autonomous goal continuation budget must be positive.");
        }

        // At least one finite backstop must survive. The budget's whole purpose is to guarantee
        // termination when the judge and the stuck detector both fail; disabling both dimensions
        // removes the only unconditional stop and makes a run genuinely unbounded.
        if (wallClockLimit is null && continuationLimit is null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wallClockLimit),
                "An autonomous goal must keep at least one finite budget; disabling both removes the termination guarantee.");
        }
    }

    private static bool IsNoLimit(string text) =>
        string.Equals(text?.Trim(), "none", StringComparison.OrdinalIgnoreCase)
        || string.Equals(text?.Trim(), "unlimited", StringComparison.OrdinalIgnoreCase)
        || string.Equals(text?.Trim(), "off", StringComparison.OrdinalIgnoreCase);
}
