namespace Cortex.Contained.Contracts.Channels;

/// <summary>
/// Extended interface for channels that require a pairing flow (e.g., QR code, OAuth link).
/// </summary>
public interface IChannelWithPairing : IChannel
{
    /// <summary>Whether the channel needs pairing before it can operate.</summary>
    bool RequiresPairing { get; }

    /// <summary>Initiate the pairing process (e.g., generate QR code).</summary>
    Task<PairingInfo> StartPairingAsync(CancellationToken ct = default);

    /// <summary>Raised during pairing to provide data (QR code, link, etc.).</summary>
    event Func<PairingData, Task>? PairingDataAvailable;

    /// <summary>Raised when pairing completes or fails.</summary>
    event Func<PairingResult, Task>? PairingCompleted;
}

/// <summary>Information about a pairing session.</summary>
public sealed record PairingInfo
{
    /// <summary>Unique pairing session ID.</summary>
    public required string SessionId { get; init; }

    /// <summary>The type of pairing (QR code, link, etc.).</summary>
    public required PairingType Type { get; init; }
}

/// <summary>Data provided during the pairing process.</summary>
public sealed record PairingData
{
    /// <summary>The type of data (QR code, URL, etc.).</summary>
    public required PairingType Type { get; init; }

    /// <summary>The pairing data (QR code string, URL, etc.).</summary>
    public required string Data { get; init; }

    /// <summary>When this data expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Result of a pairing attempt.</summary>
public sealed record PairingResult
{
    /// <summary>Whether pairing succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Error message if pairing failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The paired device/account identifier.</summary>
    public string? PairedId { get; init; }
}

/// <summary>Type of pairing mechanism.</summary>
public enum PairingType
{
    QrCode = 0,
    Link = 1,
    Code = 2
}
