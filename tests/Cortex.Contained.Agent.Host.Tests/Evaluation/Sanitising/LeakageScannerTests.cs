using Cortex.Contained.Agent.Host.Evaluation.Sanitising;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Sanitising;

public sealed class LeakageScannerTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, PseudonymMap.SecretBytes).Select(i => (byte)i).ToArray();

    private static readonly SanitiserLexicon Lexicon = SanitiserLexicon.Parse(
    [
        """{"term":"Krøllvik","kind":"place"}""",
        """{"term":"Alvenia","kind":"person"}""",
        """{"term":"example.org","kind":"public-host"}""",
    ]);

    [Fact]
    public void Scan_SanitisedText_Passes()
    {
        var sanitiser = new TextSanitiser(Lexicon, new PseudonymMap(Secret));
        var output = sanitiser.Sanitise(
            "Alvenia (a.person@example.com, +45 12 34 56 78, cpr 010203-1234) posted https://example.org/x and http://blog.test/y from Krøllvik, id 123456789012345678, @night_owl");
        var scanner = new LeakageScanner(Lexicon, sanitiser.Pseudonyms);

        scanner.Scan("messages.Content", output);

        Assert.True(scanner.Passed, string.Join("; ", scanner.Findings));
    }

    [Theory]
    [InlineData("we drove to Krollvik", "dictionary-term")]
    [InlineData("ALVENIAS bike", "dictionary-term")]
    [InlineData("mail a.person@example.com", "email")]
    [InlineData("cpr 010203-1234", "cpr")]
    [InlineData("id 123456789012345678", "number")]
    [InlineData("ping @night_owl", "handle")]
    [InlineData("see http://blog.test/y", "url-host")]
    [InlineData("we drove to Krø\u200Bllvik", "dictionary-term")]
    public void Scan_ResidualPersonalValue_FoundByKindWithoutText(string text, string kind)
    {
        var scanner = new LeakageScanner(Lexicon, []);

        scanner.Scan("history.dataset.text", text);

        Assert.False(scanner.Passed);
        var finding = Assert.Single(scanner.Findings);
        Assert.Equal(new LeakageFinding("history.dataset.text", kind, 1), finding);
    }

    [Fact]
    public void Scan_WordThatOnlyStartsLikeATerm_NotFlagged()
    {
        var scanner = new LeakageScanner(Lexicon, []);

        scanner.Scan("messages.Content", "alvenianisation of the krollvikskyline");

        Assert.True(scanner.Passed);
    }
}
