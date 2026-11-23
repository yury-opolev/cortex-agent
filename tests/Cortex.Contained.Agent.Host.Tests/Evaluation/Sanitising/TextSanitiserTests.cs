using Cortex.Contained.Agent.Host.Evaluation.Sanitising;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Sanitising;

public sealed class TextSanitiserTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, PseudonymMap.SecretBytes).Select(i => (byte)i).ToArray();

    private static SanitiserLexicon Dictionary(params string[] lines) => SanitiserLexicon.Parse(lines);

    private static TextSanitiser Sanitiser(SanitiserLexicon? dictionary = null, byte[]? secret = null) =>
        new(dictionary ?? Dictionary(
            """{"term":"Alvenia","kind":"person"}""",
            """{"term":"Vena","kind":"person","entity":"alvenia"}""",
            """{"term":"Krøllvik","kind":"place"}""",
            """{"term":"example.org","kind":"public-host"}"""),
            new PseudonymMap(secret ?? Secret));

    [Fact]
    public void Sanitise_NamesAndVariants_OneConsistentPseudonymWithInflectionKept()
    {
        var sanitiser = Sanitiser();

        var first = sanitiser.Sanitise("Alvenia and Vena met in Krøllvik; Alvenias bike");
        var second = sanitiser.Sanitise("ALVENIA lives in Krollvik");

        var person = sanitiser.Sanitise("Alvenia");
        Assert.StartsWith("Person", person, StringComparison.Ordinal);
        Assert.Equal($"{person} and {person} met in {sanitiser.Sanitise("Krøllvik")}; {person}s bike", first);
        Assert.Equal($"{person} lives in {sanitiser.Sanitise("Krøllvik")}", second);
        Assert.DoesNotContain("lvenia", first + second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitise_Identifiers_ReplacedByStablePseudonymsDatesAndPricesKept()
    {
        var sanitiser = Sanitiser();
        const string text = "On 2026-05-04 at 14:30 I paid 129.95 to DK5000400440116243, mail a.person@example.com, call +45 12 34 56 78, cpr 010203-1234, <@123456789012345678>";

        var output = sanitiser.Sanitise(text);

        Assert.Contains("On 2026-05-04 at 14:30 I paid 129.95 to iban", output, StringComparison.Ordinal);
        Assert.Contains("@example.invalid", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a.person", output, StringComparison.Ordinal);
        Assert.DoesNotContain("12 34 56 78", output, StringComparison.Ordinal);
        Assert.DoesNotContain("010203", output, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789012345678", output, StringComparison.Ordinal);
        Assert.Matches(@"<@[1-9]\d{17}>", output);
        Assert.Equal(output, sanitiser.Sanitise(text));
    }

    [Fact]
    public void Sanitise_Urls_PublicHostKeptPrivateHostAndPathsPseudonymised()
    {
        var sanitiser = Sanitiser();

        var output = sanitiser.Sanitise("see https://example.org/users/alvenia?id=7 and http://family-blog.test/2026/trip");

        Assert.StartsWith("see https://example.org/p", output, StringComparison.Ordinal);
        Assert.Contains("http://host", output, StringComparison.Ordinal);
        Assert.Contains(".example/p", output, StringComparison.Ordinal);
        Assert.DoesNotContain("alvenia", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("family-blog", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_HandlesAndZeroWidthObfuscation_Caught()
    {
        var sanitiser = Sanitiser();

        var output = sanitiser.Sanitise("ping @night_owl and Alve\u200Bnia");

        Assert.DoesNotContain("night_owl", output, StringComparison.Ordinal);
        Assert.Contains("@user", output, StringComparison.Ordinal);
        Assert.Equal(sanitiser.Sanitise("Alvenia"), output[(output.LastIndexOf(' ') + 1)..]);
    }

    [Fact]
    public void Sanitise_AnotherSecret_UnlinkablePseudonyms()
    {
        var other = Enumerable.Range(2, PseudonymMap.SecretBytes).Select(i => (byte)i).ToArray();

        Assert.NotEqual(Sanitiser().Sanitise("Alvenia"), Sanitiser(secret: other).Sanitise("Alvenia"));
    }

    [Fact]
    public void Parse_BadLines_NamedInTheError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Dictionary(
            """{"term":"Alvenia","kind":"person"}""", """{"term":"A","kind":"person"}""", """{"term":"Bo","kind":"pet"}""", "not json"));

        Assert.Contains("lines 2, 3, 4", error.Message, StringComparison.Ordinal);
    }
}
