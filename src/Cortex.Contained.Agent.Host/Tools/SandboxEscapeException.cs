namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Thrown when a user-supplied path attempts to escape the sandbox root —
/// via UNC/device paths, parent-directory (<c>..</c>) traversal, or a symlink
/// whose target resolves outside the sandbox. Derives from
/// <see cref="ArgumentException"/> so existing tool error handling (which
/// already surfaces <see cref="ArgumentException"/> as a tool failure) continues
/// to work, while allowing callers to specifically detect and audit escape attempts.
/// </summary>
public sealed class SandboxEscapeException : ArgumentException
{
    /// <summary>Initializes a new instance with a descriptive message.</summary>
    /// <param name="message">The reason the path was rejected.</param>
    public SandboxEscapeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a descriptive message and the offending parameter name.</summary>
    /// <param name="message">The reason the path was rejected.</param>
    /// <param name="paramName">The name of the parameter carrying the offending path.</param>
    public SandboxEscapeException(string message, string? paramName)
        : base(message, paramName)
    {
    }
}
