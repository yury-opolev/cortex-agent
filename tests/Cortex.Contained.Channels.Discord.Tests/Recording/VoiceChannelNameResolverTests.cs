using Cortex.Contained.Channels.Discord.Recording;
using Cortex.Contained.Contracts.Recording;

namespace Cortex.Contained.Channels.Discord.Tests.Recording;

public class VoiceChannelNameResolverTests
{
    private static readonly IReadOnlyList<(string Name, ulong Id)> Channels = new[]
    {
        ("General", 100UL),
        ("Music", 200UL),
        ("Music", 201UL),
    };

    [Fact]
    public void Host_ResolvesToHostKey()
        => Assert.Equal(
            new ResolveResult.Resolved(ChannelKey.Host, ChannelKey.Host),
            VoiceChannelNameResolver.Resolve("host", Channels));

    [Fact]
    public void HostUppercase_ResolvesToHostKey()
        => Assert.Equal(
            new ResolveResult.Resolved(ChannelKey.Host, ChannelKey.Host),
            VoiceChannelNameResolver.Resolve("HOST", Channels));

    [Fact]
    public void UniqueName_ResolvesByName()
        => Assert.Equal(
            new ResolveResult.Resolved("discord:100", "General"),
            VoiceChannelNameResolver.Resolve("General", Channels));

    [Fact]
    public void Ambiguous_ReturnsAmbiguous()
        => Assert.IsType<ResolveResult.Ambiguous>(
            VoiceChannelNameResolver.Resolve("Music", Channels));

    [Fact]
    public void Unknown_ReturnsNotFound()
        => Assert.IsType<ResolveResult.NotFound>(
            VoiceChannelNameResolver.Resolve("Foo", Channels));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmpty_FallsBackToCurrent(string? input)
        => Assert.IsType<ResolveResult.FallbackToCurrent>(
            VoiceChannelNameResolver.Resolve(input, Channels));
}
