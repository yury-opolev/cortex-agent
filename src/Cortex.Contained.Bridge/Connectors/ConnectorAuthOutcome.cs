namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Outcome of a connector authentication attempt.</summary>
public enum ConnectorAuthOutcome
{
    /// <summary>The connector is approved and may proceed to the ready state.</summary>
    Approved,

    /// <summary>The connector must complete the pairing flow before being accepted.</summary>
    PairingRequired,

    /// <summary>The connector is denied and the session must be closed.</summary>
    Denied,
}
