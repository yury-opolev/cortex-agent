using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Pure resolution of the coda binary path from the configured <see cref="CodaSource"/>.
/// Bundled path is <c>&lt;baseDirectory&gt;/coda/coda.exe</c> (next to the Bridge).
/// </summary>
public static class CodaBinaryResolver
{
    public static (string Path, bool FellBackFromBundle) Resolve(
        CodaSource source, string baseDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var bundled = System.IO.Path.Combine(baseDirectory, "coda", "coda.exe");

        return source switch
        {
            CodaSource.Host => ("coda", false),
            CodaSource.Bundled => fileExists(bundled) ? (bundled, false) : ("coda", true),
            _ => fileExists(bundled) ? (bundled, false) : ("coda", false), // Auto
        };
    }
}
