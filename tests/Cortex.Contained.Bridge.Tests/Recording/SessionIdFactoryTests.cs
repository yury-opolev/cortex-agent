using Cortex.Contained.Bridge.Recording;

namespace Cortex.Contained.Bridge.Tests.Recording;

public class SessionIdFactoryTests
{
    private static readonly DateTimeOffset SampleUtc =
        new(2026, 5, 20, 19, 12, 23, TimeSpan.Zero);

    [Fact]
    public void Create_UsesUtcStampWithLabel()
        => Assert.Equal("trailing-off-20260520-191223",
            SessionIdFactory.Create("trailing-off", SampleUtc));

    [Fact]
    public void Create_NullLabel_DefaultsToSession()
        => Assert.Equal("session-20260520-191223",
            SessionIdFactory.Create(null, SampleUtc));

    [Fact]
    public void Create_WhitespaceLabel_DefaultsToSession()
        => Assert.Equal("session-20260520-191223",
            SessionIdFactory.Create("   ", SampleUtc));

    [Theory]
    [InlineData("with spaces", "with-spaces")]
    [InlineData("a/b\\c", "a-b-c")]
    [InlineData("OK_label-1", "OK_label-1")]
    [InlineData("!!! ", "session")]
    [InlineData("...drop-leading-and-trailing...", "drop-leading-and-trailing")]
    [InlineData("multi---runs", "multi---runs")] // existing dashes preserved
    public void Sanitise_KeepsAlnumDashUnderscore(string raw, string expected)
        => Assert.Equal(expected, SessionIdFactory.Sanitise(raw));
}
