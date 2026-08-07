namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Result of <see cref="IConnectorRegistry.TryAttachAsync"/>.</summary>
public sealed record ConnectorAttachResult
{
    /// <summary>Whether the channel was successfully attached.</summary>
    public required bool Success { get; init; }

    /// <summary>Machine-readable error code; one of <see cref="ConnectorErrorCodes"/>. Null on success.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error description. Null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a successful attach result.</summary>
    public static ConnectorAttachResult Ok() => new() { Success = true };

    /// <summary>Creates a failed attach result with a code and message.</summary>
    public static ConnectorAttachResult Failed(string code, string message) =>
        new() { Success = false, ErrorCode = code, ErrorMessage = message };
}
