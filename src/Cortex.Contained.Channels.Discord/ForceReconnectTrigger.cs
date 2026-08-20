namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure selection of the watchdog force-reconnect trigger label from the active
/// suspect flags. MLS-failure takes precedence (it re-speaks a pending proactive
/// message), then a stalled DAVE handshake, then decrypt-flood, else a silent
/// audio-transport death.
/// </summary>
public static class ForceReconnectTrigger
{
    public static string Resolve(bool daveMlsSuspect, bool decryptFloodSuspect, bool daveHandshakeStallSuspect = false)
    {
        if (daveMlsSuspect)
        {
            return "dave-mls-failure";
        }

        if (daveHandshakeStallSuspect)
        {
            return "dave-handshake-stall";
        }

        if (decryptFloodSuspect)
        {
            return "dave-decrypt-flood";
        }

        return "audio-death-signal";
    }
}
