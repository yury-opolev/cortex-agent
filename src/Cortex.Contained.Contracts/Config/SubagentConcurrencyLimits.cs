namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// Shared bounds for the subagent concurrency cap (<c>MaxConcurrentSubagents</c>), used by
/// <see cref="AgentConfig"/>, <see cref="BridgeConfig"/>, <c>SubagentRunnerRegistry</c>, and the
/// Bridge settings endpoint so every boundary agrees on the same range. Values outside the range
/// are always REJECTED — never silently clamped.
/// </summary>
public static class SubagentConcurrencyLimits
{
    public const int Minimum = 1;
    public const int Default = 5;
    public const int Maximum = 50;

    /// <summary>True when <paramref name="value"/> is within [<see cref="Minimum"/>, <see cref="Maximum"/>].</summary>
    public static bool IsValid(int value)
        => value is >= Minimum and <= Maximum;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when <paramref name="value"/> is out of range.</summary>
    public static void ThrowIfInvalid(int value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {Minimum} and {Maximum}.");
        }
    }
}
