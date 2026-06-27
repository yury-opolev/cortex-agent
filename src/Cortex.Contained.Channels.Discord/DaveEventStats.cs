using System.Text.RegularExpressions;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Classifies a single DAVE-related diagnostic log message emitted by
/// <c>Discord.Net</c>'s voice pipeline.
/// </summary>
public enum DaveEventKind
{
    None = 0,

    /// <summary>Native <c>libdave</c> returned <c>DecryptorResultCode.DecryptionFailure</c> (code 1).</summary>
    DecryptFailure,

    /// <summary>Native <c>libdave</c> returned <c>DecryptorResultCode.MissingKeyRatchet</c> (code 2).</summary>
    MissingKeyRatchet,

    /// <summary>Native <c>libdave</c> returned <c>DecryptorResultCode.InvalidNonce</c> (code 3).</summary>
    InvalidNonce,

    /// <summary>Native <c>libdave</c> returned <c>DecryptorResultCode.MissingCryptor</c> (code 4).</summary>
    MissingCryptor,

    /// <summary>RTP packet had a payload type other than Discord's dynamic voice type (120).</summary>
    MalformedFrame,

    /// <summary>RTP SSRC could not be mapped to a known user.</summary>
    UnknownSsrc,

    /// <summary>Packet had a known SSRC but no matching stream.</summary>
    UnknownUser,

    /// <summary>MLS session-layer failure during DAVE handshake / epoch transition.</summary>
    MlsFailure,
}

/// <summary>
/// Per-category counters for DAVE voice-decoding failures. Thread-safe:
/// all mutation happens through <see cref="Interlocked"/> primitives.
/// Supports cheap snapshot semantics so callers can diff across arbitrary
/// time windows (e.g. "how many decrypt failures during this single user
/// turn").
/// </summary>
public sealed class DaveEventStats
{
    private long decryptFailure;
    private long missingKeyRatchet;
    private long invalidNonce;
    private long missingCryptor;
    private long malformedFrame;
    private long unknownSsrc;
    private long unknownUser;
    private long mlsFailure;

    /// <summary>Immutable snapshot of the counters at a moment in time.</summary>
    public readonly record struct Snapshot(
        long DecryptFailure,
        long MissingKeyRatchet,
        long InvalidNonce,
        long MissingCryptor,
        long MalformedFrame,
        long UnknownSsrc,
        long UnknownUser,
        long MlsFailure)
    {
        /// <summary>Sum of all counters in this snapshot.</summary>
        public long Total =>
            this.DecryptFailure
            + this.MissingKeyRatchet
            + this.InvalidNonce
            + this.MissingCryptor
            + this.MalformedFrame
            + this.UnknownSsrc
            + this.UnknownUser
            + this.MlsFailure;

        /// <summary>Returns this minus <paramref name="other"/>, clamped at zero.</summary>
        public Snapshot Delta(Snapshot other) => new(
            Math.Max(0, this.DecryptFailure - other.DecryptFailure),
            Math.Max(0, this.MissingKeyRatchet - other.MissingKeyRatchet),
            Math.Max(0, this.InvalidNonce - other.InvalidNonce),
            Math.Max(0, this.MissingCryptor - other.MissingCryptor),
            Math.Max(0, this.MalformedFrame - other.MalformedFrame),
            Math.Max(0, this.UnknownSsrc - other.UnknownSsrc),
            Math.Max(0, this.UnknownUser - other.UnknownUser),
            Math.Max(0, this.MlsFailure - other.MlsFailure));
    }

    /// <summary>Atomically increment the counter for the given event kind.</summary>
    public void Record(DaveEventKind kind)
    {
        switch (kind)
        {
            case DaveEventKind.DecryptFailure: Interlocked.Increment(ref this.decryptFailure); break;
            case DaveEventKind.MissingKeyRatchet: Interlocked.Increment(ref this.missingKeyRatchet); break;
            case DaveEventKind.InvalidNonce: Interlocked.Increment(ref this.invalidNonce); break;
            case DaveEventKind.MissingCryptor: Interlocked.Increment(ref this.missingCryptor); break;
            case DaveEventKind.MalformedFrame: Interlocked.Increment(ref this.malformedFrame); break;
            case DaveEventKind.UnknownSsrc: Interlocked.Increment(ref this.unknownSsrc); break;
            case DaveEventKind.UnknownUser: Interlocked.Increment(ref this.unknownUser); break;
            case DaveEventKind.MlsFailure: Interlocked.Increment(ref this.mlsFailure); break;
            default: break;
        }
    }

    /// <summary>Take a consistent snapshot of all counters.</summary>
    public Snapshot Take() => new(
        Interlocked.Read(ref this.decryptFailure),
        Interlocked.Read(ref this.missingKeyRatchet),
        Interlocked.Read(ref this.invalidNonce),
        Interlocked.Read(ref this.missingCryptor),
        Interlocked.Read(ref this.malformedFrame),
        Interlocked.Read(ref this.unknownSsrc),
        Interlocked.Read(ref this.unknownUser),
        Interlocked.Read(ref this.mlsFailure));

    /// <summary>
    /// Parses a <c>Discord.Net</c> log message and classifies it, returning
    /// <see cref="DaveEventKind.None"/> for anything that isn't a DAVE event
    /// we care about.
    /// </summary>
    /// <remarks>
    /// Matches the log strings emitted by Discord.Net 3.19.x —
    /// <list type="bullet">
    ///   <item><c>"Failed to decrypt audio packet for {userId}: {DecryptorResultCode}"</c> (DaveDecryptStream)</item>
    ///   <item><c>"Malformed Frame"</c> (AudioClient — RTP type != 120)</item>
    ///   <item><c>"Unknown SSRC {ssrc}"</c> (AudioClient)</item>
    ///   <item><c>"Unknown User {userId}"</c> (AudioClient)</item>
    ///   <item><c>"MLS Failure: ..."</c> (DaveSessionManager)</item>
    /// </list>
    /// </remarks>
    public static DaveEventKind Classify(string? source, string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return DaveEventKind.None;
        }

        if (message.StartsWith("Failed to decrypt audio packet", StringComparison.Ordinal))
        {
            // The message ends in ": {code}" — cheap case-sensitive contains check
            // avoids allocating a split.
            if (message.EndsWith(": DecryptionFailure", StringComparison.Ordinal))
            {
                return DaveEventKind.DecryptFailure;
            }
            if (message.EndsWith(": MissingKeyRatchet", StringComparison.Ordinal))
            {
                return DaveEventKind.MissingKeyRatchet;
            }
            if (message.EndsWith(": InvalidNonce", StringComparison.Ordinal))
            {
                return DaveEventKind.InvalidNonce;
            }
            if (message.EndsWith(": MissingCryptor", StringComparison.Ordinal))
            {
                return DaveEventKind.MissingCryptor;
            }
            // Unknown code — still a decrypt failure at heart.
            return DaveEventKind.DecryptFailure;
        }

        if (message.StartsWith("Malformed Frame", StringComparison.Ordinal))
        {
            return DaveEventKind.MalformedFrame;
        }

        if (message.StartsWith("Unknown SSRC", StringComparison.Ordinal))
        {
            return DaveEventKind.UnknownSsrc;
        }

        if (message.StartsWith("Unknown User", StringComparison.Ordinal))
        {
            return DaveEventKind.UnknownUser;
        }

        if (message.StartsWith("MLS Failure", StringComparison.Ordinal))
        {
            return DaveEventKind.MlsFailure;
        }

        return DaveEventKind.None;
    }

    /// <summary>
    /// Extracts the userId from a "Failed to decrypt audio packet for {userId}: ..."
    /// message. Returns null if the message doesn't match or the userId can't
    /// be parsed.
    /// </summary>
    public static ulong? TryParseUserId(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;

        var match = UserIdPattern.Match(message);
        if (!match.Success) return null;

        return ulong.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    private static readonly Regex UserIdPattern = new(
        @"Failed to decrypt audio packet for (\d+):",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
