namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// The tenant's storage master key (256-bit, Base64), pushed by the Bridge from DPAPI storage
/// before credentials. The agent derives one key per database from it and holds it in memory only.
/// </summary>
public sealed record StorageKeyGrant
{
    /// <summary>Base64 of 32 random bytes.</summary>
    public required string Key { get; init; }
}
