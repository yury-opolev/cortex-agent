using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>
/// Deterministic pseudonyms for one frozen corpus version: HMAC-SHA256 under the version's secret, so the same
/// person, place or identifier gets the same pseudonym in the corpus, the memories and the query datasets
/// (relevance labels stay valid), different versions are unlinkable, and nobody without the local secret can
/// reverse or confirm a guess. The secret never leaves the machine (it is not part of the export package).
/// Two different values that would share a pseudonym fail the freeze (fail closed).
/// </summary>
public sealed class PseudonymMap
{
    /// <summary>Length of a corpus secret.</summary>
    public const int SecretBytes = 32;

    private const int TokenChars = 8;

    // 32 symbols without l, o, 0 and 1.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    private static readonly Regex TokenShape = new(
        "^(?:person|place|org|user|mail|host|term|phone|cpr|iban|card|gps|p)[a-km-np-z2-9]{8}\\p{L}{0,3}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly byte[] secret;
    private readonly Dictionary<string, string> byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> owners = new(StringComparer.Ordinal);

    public PseudonymMap(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != SecretBytes)
        {
            throw new ArgumentException($"The corpus secret must have {SecretBytes} bytes.", nameof(secret));
        }

        this.secret = (byte[])secret.Clone();
    }

    /// <summary>Every pseudonym handed out so far (the leakage check never flags them).</summary>
    public IReadOnlyCollection<string> Pseudonyms => this.owners.Keys;

    /// <summary>
    /// True when <paramref name="run"/> (a run of letters and digits) has the shape of a produced pseudonym: a kind
    /// prefix, eight token symbols and at most three kept inflection letters. Lets the leakage check ignore a short
    /// dictionary term that happens to sit between two digits of a token.
    /// </summary>
    public static bool IsPseudonymToken(ReadOnlySpan<char> run) => TokenShape.IsMatch(run);

    /// <summary>A fresh random corpus secret.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>
    /// The pseudonym of <paramref name="key"/> (a canonical value) of <paramref name="kind"/>: <c>Person…</c>,
    /// <c>Place…</c>, <c>Org…</c>, <c>user…</c>, <c>Term…</c>, <c>mail…@example.invalid</c>, <c>phone…</c>,
    /// <c>cpr…</c>, <c>iban…</c>, <c>card…</c>, <c>gps…</c>, <c>host….example</c>, <c>p…</c>; <c>number</c> keeps the
    /// digit count. Each is a single token for the keyword index.
    /// </summary>
    public string For(string kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(key);
        var id = kind + "\n" + key;
        if (this.byKey.TryGetValue(id, out var known))
        {
            return known;
        }

        var token = this.Token(id);
        var pseudonym = kind switch
        {
            "person" => "Person" + token,
            "place" => "Place" + token,
            "org" => "Org" + token,
            "handle" => "user" + token,
            "email" => "mail" + token + "@example.invalid",
            "host" => "host" + token + ".example",
            "path" => "p" + token,
            "number" => this.Digits(id, key.Length),
            _ => (kind == "other" ? "Term" : kind) + token,
        };
        if (this.owners.TryGetValue(pseudonym, out var owner) && !string.Equals(owner, id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Two values of the corpus received the same pseudonym; freeze again (a new version gets a new secret).");
        }

        this.owners[pseudonym] = id;
        this.byKey[id] = pseudonym;
        return pseudonym;
    }

    private string Token(string id)
    {
        var hash = HMACSHA256.HashData(this.secret, Encoding.UTF8.GetBytes(id));
        var chars = new char[TokenChars];
        for (var i = 0; i < TokenChars; i++)
        {
            chars[i] = Alphabet[hash[i] % Alphabet.Length];
        }

        return new string(chars);
    }

    // Same length, first digit never 0; derived from the HMAC so equal numbers stay equal.
    private string Digits(string id, int length)
    {
        var builder = new StringBuilder(length);
        var counter = 0;
        while (builder.Length < length)
        {
            var hash = HMACSHA256.HashData(this.secret, Encoding.UTF8.GetBytes(id + "\n" + counter++));
            foreach (var b in hash)
            {
                if (builder.Length == length)
                {
                    break;
                }

                builder.Append(builder.Length == 0 ? (char)('1' + (b % 9)) : (char)('0' + (b % 10)));
            }
        }

        return builder.ToString();
    }
}
