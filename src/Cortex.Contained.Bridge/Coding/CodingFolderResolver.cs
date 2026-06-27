namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Pure helper for testing whether a candidate path lies inside any of a list of allowed roots.
/// Both sides are normalized via <see cref="Path.GetFullPath(string)"/> and compared with
/// case-insensitive Windows-aware semantics. Symlinks are not resolved.
/// </summary>
public static class CodingFolderResolver
{
    /// <summary>
    /// Returns true if <paramref name="candidate"/> is exactly one of <paramref name="allowedRoots"/>
    /// or a descendant of one of them.
    /// </summary>
    public static bool IsPathInsideAny(string candidate, IReadOnlyList<string> allowedRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentNullException.ThrowIfNull(allowedRoots);

        if (allowedRoots.Count == 0)
        {
            return false;
        }

        var normalizedCandidate = NormalizePath(candidate);

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalizedRoot = NormalizePath(root);
            if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Use the directory separator to ensure prefix matches do not cross folder boundaries
            // (e.g. C:\foo should NOT match C:\foobar).
            var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;

            if (normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Canonicalizes a folder path (absolute, trailing separators stripped) for stable
    /// comparison. Shared by <see cref="CodingFoldersStore"/> so both normalize identically.
    /// </summary>
    internal static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
