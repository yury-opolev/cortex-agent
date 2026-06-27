namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Permission policy for a coding session. Ordered from most restrictive (Prompt) to least
/// (Yolo). A session may be started at or below a folder's configured ceiling, never above it.
/// </summary>
public enum CodingPolicy
{
    /// <summary>Every mutating tool raises a permission request relayed to the user.</summary>
    Prompt = 0,

    /// <summary>Bypass with a safety classifier: safe mutations auto-run, risky ones escalate.</summary>
    YoloSafe = 1,

    /// <summary>No prompts for anything.</summary>
    Yolo = 2,
}

/// <summary>Ordering helpers for <see cref="CodingPolicy"/>.</summary>
public static class CodingPolicyExtensions
{
    /// <summary>True when <paramref name="requested"/> is no more permissive than <paramref name="ceiling"/>.</summary>
    public static bool IsWithinCeiling(this CodingPolicy requested, CodingPolicy ceiling) =>
        (int)requested <= (int)ceiling;
}
