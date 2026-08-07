using System.Text.RegularExpressions;
using Cortex.Contained.Bridge.Connectors.Security;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class ConnectorTokenGeneratorTests
{
    [Fact]
    public void CreateToken_Always_ReturnsBase64UrlNoPadding()
    {
        var token = ConnectorTokenGenerator.CreateToken();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.NotEmpty(token);
    }

    [Fact]
    public void CreateToken_CalledRepeatedly_GeneratesUniqueTokens()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => ConnectorTokenGenerator.CreateToken()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(100, tokens.Count);
    }

    [Theory]
    [InlineData(1000)]
    public void CreatePairingCode_CalledRepeatedly_AlwaysMatchesShape(int count)
    {
        var pattern = new Regex("^[2-9A-HJ-NP-Z]{4}-[2-9A-HJ-NP-Z]{3}$", RegexOptions.None, TimeSpan.FromSeconds(1));

        for (var i = 0; i < count; i++)
        {
            var code = ConnectorTokenGenerator.CreatePairingCode();
            Assert.Matches(pattern, code);
        }
    }

    [Fact]
    public void CreatePairingCode_CalledRepeatedly_GeneratesDifferentCodes()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => ConnectorTokenGenerator.CreatePairingCode()).ToHashSet(StringComparer.Ordinal);

        Assert.True(codes.Count > 1, "1000 pairing codes should not all be identical");
    }

    [Fact]
    public void TokensEqual_SameValue_ReturnsTrue()
    {
        Assert.True(ConnectorTokenGenerator.TokensEqual("abc", "abc"));
    }

    [Fact]
    public void TokensEqual_DifferentValues_ReturnsFalse()
    {
        Assert.False(ConnectorTokenGenerator.TokensEqual("abc", "xyz"));
    }

    [Fact]
    public void TokensEqual_NullLeft_ReturnsFalse()
    {
        Assert.False(ConnectorTokenGenerator.TokensEqual(null, "abc"));
    }

    [Fact]
    public void TokensEqual_NullRight_ReturnsFalse()
    {
        Assert.False(ConnectorTokenGenerator.TokensEqual("abc", null));
    }

    [Fact]
    public void TokensEqual_BothNull_ReturnsFalse()
    {
        Assert.False(ConnectorTokenGenerator.TokensEqual(null, null));
    }

    [Fact]
    public void TokensEqual_BothEmpty_ReturnsTrue()
    {
        Assert.True(ConnectorTokenGenerator.TokensEqual(string.Empty, string.Empty));
    }

    [Fact]
    public void TokensEqual_DifferentLengths_ReturnsFalse()
    {
        Assert.False(ConnectorTokenGenerator.TokensEqual("abc", "ab"));
    }
}
