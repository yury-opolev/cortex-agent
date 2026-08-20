using System.Globalization;

namespace Cortex.Contained.Channels.Discord;

/// <summary>Lifecycle milestone of the DAVE (end-to-end-encryption) handshake.</summary>
public enum DaveLifecycleEvent
{
    /// <summary>Not a DAVE lifecycle line, or the session is running unencrypted.</summary>
    None = 0,

    /// <summary>A DAVE/MLS session was initialised and our key package was sent.</summary>
    HandshakeStarted,

    /// <summary>The MLS group is established and the media keys are installed.</summary>
    SessionReady,
}

/// <summary>
/// Pure classifier that recognises the two <c>Discord.Net</c> log lines which
/// bracket the DAVE handshake, so the voice watchdog can tell an established
/// end-to-end-encrypted session from one that never completed.
/// </summary>
/// <remarks>
/// Why these two lines specifically (Discord.Net 3.20.1, <c>DaveSessionManager</c>):
/// <list type="bullet">
///   <item><c>HandleDaveProtocolInitAsync</c> logs <c>"Init dave protocol session, version {v}"</c>
///   and then sends our MLS key package — the handshake has started.</item>
///   <item><c>PrepareProtocolTransitionAsync</c> logs
///   <c>"Preparing to transition to protocol version {v} (transition #{id})"</c>. This is the
///   authoritative completion point: it is only reached from a successful
///   <c>ProcessWelcome</c> / <c>ProcessCommit</c>, and it installs the per-sender decryptor
///   ratchets <em>and</em> the encryptor ratchet. So it proves the group is keyed in
///   <em>both</em> directions — which is exactly what the 2026-06-29 outage lacked.</item>
/// </list>
/// <para>
/// A protocol version of 0 (<c>Dave.DisabledProtocolVersion</c>) means the session is
/// running unencrypted; those lines are deliberately not treated as lifecycle events so
/// a DAVE-disabled deployment never arms the handshake-stall watchdog.
/// </para>
/// <para>
/// Deliberately kept out of <see cref="DaveEventStats"/>: that type counts <em>failures</em>
/// and feeds the periodic stats summary, whereas these are healthy-path milestones.
/// </para>
/// </remarks>
public static class DaveSessionLifecycleClassifier
{
    private const string InitPrefix = "Init dave protocol session, version ";
    private const string TransitionPrefix = "Preparing to transition to protocol version ";

    /// <summary>
    /// Classifies a <c>Discord.Net</c> log line. Only the <c>Dave #N</c> session-manager
    /// source is considered; returns <see cref="DaveLifecycleEvent.None"/> for anything else.
    /// </summary>
    public static DaveLifecycleEvent Classify(string? source, string? message)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(message))
        {
            return DaveLifecycleEvent.None;
        }

        // "Dave #5" is the DaveSessionManager logger. "Dave decrypt stream {id}" and
        // "Dave encrypt stream" share the prefix but never emit these messages, so the
        // message check below disambiguates.
        if (!source.StartsWith("Dave", StringComparison.OrdinalIgnoreCase))
        {
            return DaveLifecycleEvent.None;
        }

        if (message.StartsWith(InitPrefix, StringComparison.Ordinal))
        {
            return IsEncryptedVersion(message, InitPrefix.Length)
                ? DaveLifecycleEvent.HandshakeStarted
                : DaveLifecycleEvent.None;
        }

        if (message.StartsWith(TransitionPrefix, StringComparison.Ordinal))
        {
            return IsEncryptedVersion(message, TransitionPrefix.Length)
                ? DaveLifecycleEvent.SessionReady
                : DaveLifecycleEvent.None;
        }

        return DaveLifecycleEvent.None;
    }

    /// <summary>
    /// Reads the protocol-version number that follows <paramref name="start"/> and reports
    /// whether it denotes an encrypted session (non-zero).
    /// </summary>
    private static bool IsEncryptedVersion(string message, int start)
    {
        var end = start;
        while (end < message.Length && char.IsAsciiDigit(message[end]))
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        return ushort.TryParse(
            message.AsSpan(start, end - start),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var version) && version > 0;
    }
}
