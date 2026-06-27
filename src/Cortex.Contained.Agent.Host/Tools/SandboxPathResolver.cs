namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Resolves and validates file paths within a sandbox root directory.
/// Prevents path traversal attacks (../ escape) and symlink escapes.
/// All paths are resolved to absolute paths within the sandbox root.
/// </summary>
internal static class SandboxPathResolver
{
    /// <summary>
    /// Optional security-audit hook invoked with a reason string every time an escape
    /// attempt is blocked, BEFORE the <see cref="SandboxEscapeException"/> is thrown.
    /// Wired to a logger at startup so attempts are recorded even when a tool's own
    /// <c>catch (ArgumentException)</c> swallows the exception downstream.
    /// </summary>
    internal static Action<string>? EscapeAudit { get; set; }

    private static SandboxEscapeException Blocked(string reason, string paramName)
    {
        EscapeAudit?.Invoke(reason);
        return new SandboxEscapeException(reason, paramName);
    }

    /// <summary>
    /// Resolve a user-provided path to a safe absolute path within the sandbox root.
    /// </summary>
    /// <param name="sandboxRoot">The absolute path to the sandbox root directory (e.g., /app/data).</param>
    /// <param name="userPath">The user-provided relative or absolute path.</param>
    /// <returns>The resolved absolute path.</returns>
    /// <exception cref="ArgumentException">If the path is empty, invalid, or escapes the sandbox.</exception>
    internal static string Resolve(string sandboxRoot, string userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(userPath));
        }

        // Reject UNC and device paths up front, on the raw user-supplied string —
        // before any separator normalization. This covers UNC shares (\\server\share),
        // and the Windows device/long-path prefixes \\?\ and \\.\, as well as their
        // forward-slash equivalents. Rooted non-sandbox paths are caught by the root
        // check below, so they are intentionally not handled here.
        if (userPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            userPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw Blocked("UNC and device paths are not allowed.", nameof(userPath));
        }

        // Normalize the sandbox root to a full path with trailing separator
        var rootFull = Path.GetFullPath(sandboxRoot);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        // Strip leading slashes so Path.Combine treats it as relative
        var cleaned = userPath
            .Replace('\\', '/')
            .TrimStart('/');

        // Reject obviously malicious patterns before resolution
        if (cleaned.Contains(".."))
        {
            // Still attempt resolution -- Path.GetFullPath normalizes ".."
            // We'll catch escapes in the final check below
        }

        // Combine and resolve to absolute
        var combined = Path.Combine(rootFull, cleaned);
        var resolved = Path.GetFullPath(combined);

        // Ensure the resolved path is within the sandbox root
        if (!resolved.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(resolved, rootFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw Blocked($"Path escapes sandbox: {userPath}", nameof(userPath));
        }

        return resolved;
    }

    /// <summary>
    /// Resolve a path and also verify that the target exists on disk.
    /// If the resolved path is a symlink, verify the symlink target also stays within the sandbox.
    /// </summary>
    internal static string ResolveAndVerify(string sandboxRoot, string userPath)
    {
        var resolved = Resolve(sandboxRoot, userPath);

        // If the path exists as a symlink, check where it points
        var fileInfo = new FileInfo(resolved);
        if (fileInfo.Exists && fileInfo.LinkTarget is not null)
        {
            var linkTarget = Path.GetFullPath(fileInfo.LinkTarget, Path.GetDirectoryName(resolved)!);
            var rootFull = Path.GetFullPath(sandboxRoot);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            {
                rootFull += Path.DirectorySeparatorChar;
            }

            if (!linkTarget.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw Blocked($"Symlink escapes sandbox: {userPath}", nameof(userPath));
            }
        }

        var dirInfo = new DirectoryInfo(resolved);
        if (dirInfo.Exists && dirInfo.LinkTarget is not null)
        {
            var linkTarget = Path.GetFullPath(dirInfo.LinkTarget, Path.GetDirectoryName(resolved)!);
            var rootFull = Path.GetFullPath(sandboxRoot);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            {
                rootFull += Path.DirectorySeparatorChar;
            }

            if (!linkTarget.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw Blocked($"Symlink escapes sandbox: {userPath}", nameof(userPath));
            }
        }

        return resolved;
    }
}
