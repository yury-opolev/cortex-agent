using Cortex.Contained.Bridge.Mcp.Auth;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpPkceTests
{
    [Fact]
    public void ComputeChallenge_Rfc7636TestVector_MatchesExpected()
    {
        // RFC 7636 Appendix B canonical verifier/challenge pair.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        var challenge = McpPkce.ComputeChallenge(verifier);

        Assert.Equal(expectedChallenge, challenge);
    }

    [Fact]
    public void ComputeChallenge_IsBase64UrlNoPadding()
    {
        var challenge = McpPkce.ComputeChallenge("some-verifier-value-1234567890");

        Assert.DoesNotContain('=', challenge);
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
    }

    [Fact]
    public void Generate_ProducesValidVerifierAndMatchingChallenge()
    {
        var pair = McpPkce.Generate();

        // RFC 7636 §4.1: verifier is 43..128 chars from the unreserved set.
        Assert.InRange(pair.Verifier.Length, 43, 128);
        Assert.DoesNotContain('=', pair.Verifier);
        Assert.DoesNotContain('+', pair.Verifier);
        Assert.DoesNotContain('/', pair.Verifier);
        Assert.Equal(McpPkce.ComputeChallenge(pair.Verifier), pair.Challenge);
    }

    [Fact]
    public void Generate_TwoCalls_ProduceDifferentVerifiers()
    {
        var a = McpPkce.Generate();
        var b = McpPkce.Generate();

        Assert.NotEqual(a.Verifier, b.Verifier);
    }
}
