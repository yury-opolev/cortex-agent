namespace Cortex.Contained.Contracts.Coding;

/// <summary>Which coda binary the Bridge launches for coding sessions.</summary>
public enum CodaSource
{
    /// <summary>Bundled coda if present, else the host <c>coda</c> on PATH.</summary>
    Auto,

    /// <summary>Always the host-installed <c>coda</c> (PATH).</summary>
    Host,

    /// <summary>Always the bundled <c>coda.exe</c>; falls back to host if absent.</summary>
    Bundled,
}
