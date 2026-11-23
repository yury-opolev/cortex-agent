using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Contracts.Tests.Security;

public sealed class SensitiveDataRedactorPersistenceTests
{
    private static string Redact(string input, params string[] secrets) =>
        SensitiveDataRedactor.RedactForPersistence(input, secrets);

    [Theory]
    [InlineData("cpr 010203-1234 noted", "[REDACTED:cpr]")]
    [InlineData("pay to DK5000400440116243 today", "[REDACTED:iban]")]
    [InlineData("card 4111 1111 1111 1111 on file", "[REDACTED:card]")]
    [InlineData("mail a.person@example.com now", "[REDACTED:email]")]
    [InlineData("call +45 12 34 56 78 later", "[REDACTED:phone]")]
    [InlineData("call 12345678 later", "[REDACTED:phone]")]
    [InlineData("we met at 55.676098, 12.568337", "[REDACTED:gps]")]
    public void RedactForPersistence_PersonalIdentifier_Replaced(string input, string placeholder)
    {
        var output = Redact(input);

        Assert.Contains(placeholder, output, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactForPersistence_KnownSecret_ReplacedEvenWhenEscapedOrSplitByZeroWidth()
    {
        const string secret = "s3cret-value-1234";

        Assert.DoesNotContain(secret, Redact("token " + secret, secret), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Redact("\\u0073\\u0033cret-value-1234", secret), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Redact("s3cret-\u200Bvalue-1234", secret), StringComparison.Ordinal);
    }

    [Fact]
    public void RedactForPersistence_InvalidChecksums_NotTreatedAsIbanOrCard()
    {
        var output = Redact("ref DK5000400440116244 and 4111111111111112");

        Assert.DoesNotContain("[REDACTED:iban]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[REDACTED:card]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactForPersistence_OrdinaryText_Unchanged()
    {
        Assert.Equal("order 42 shipped on monday", Redact("order 42 shipped on monday"));
    }

    [Fact]
    public void RedactForPersistence_StillAppliesLogPatterns()
    {
        Assert.Contains("[REDACTED]", Redact("api_key=abcdefghijklmnop"), StringComparison.Ordinal);
    }

    [Fact]
    public void FindPersonalIdentifiers_SpansOfTheInputInPriorityOrder()
    {
        const string input = "card 4111 1111 1111 1111, cpr 010203-1234, mail a.person@example.com, call +45 12 34 56 78, ref 41234567890";

        var matches = SensitiveDataRedactor.FindPersonalIdentifiers(input);

        Assert.Equal(
            ["card:4111 1111 1111 1111", "cpr:010203-1234", "email:a.person@example.com", "phone:+45 12 34 56 78", "phone:41234567890"],
            matches.Select(m => $"{m.Kind}:{input.AsSpan(m.Index, m.Length)}"));
    }

    [Fact]
    public void FindPersonalIdentifiers_OrdinaryText_None()
    {
        Assert.Empty(SensitiveDataRedactor.FindPersonalIdentifiers("order 42 shipped on monday at 14:30 for 129.95"));
    }
}
