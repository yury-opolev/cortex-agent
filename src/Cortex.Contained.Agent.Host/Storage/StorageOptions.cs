namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Storage settings, bound from the <c>Storage</c> section (<c>cortex.yml</c> or <c>Storage__*</c>).</summary>
public sealed class StorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// Encrypt <c>messages.db</c> and the history index generations with SQLite3 Multiple Ciphers,
    /// keyed by the Bridge (D4). Off by default for the first deploy; once on, it must stay on
    /// (an older build cannot read the encrypted files).
    /// </summary>
    public bool EncryptAtRest { get; set; }

    /// <summary>How long a request waits for the storage key before failing (fail closed).</summary>
    public int KeyWaitSeconds { get; set; } = 120;
}
