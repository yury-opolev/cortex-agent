using Cortex.Contained.Bridge.RemoteServices;

namespace Cortex.Contained.Bridge.Tests.RemoteServices;

public class RemoteServiceResolverTests
{
    private readonly RemoteServiceResolver resolver = new();

    [Fact]
    public void Effective_NullOverride_ReturnsLocalDefault()
    {
        var result = this.resolver.EffectiveEmbeddingEndpoint(null);

        Assert.Equal(RemoteServiceResolver.EmbeddingsLocalDefault, result);
    }

    [Fact]
    public void Effective_BlankOverride_ReturnsLocalDefault()
    {
        var result = this.resolver.EffectiveEmbeddingEndpoint("  ");

        Assert.Equal(RemoteServiceResolver.EmbeddingsLocalDefault, result);
    }

    [Fact]
    public void Effective_Override_ReturnsTrimmedOverride()
    {
        var result = this.resolver.EffectiveEmbeddingEndpoint(" http://mac:11434 ");

        Assert.Equal("http://mac:11434", result);
    }

    [Fact]
    public void IsEmbeddingDefault_TrueForLocalDefault()
    {
        var result = this.resolver.IsEmbeddingDefault(RemoteServiceResolver.EmbeddingsLocalDefault);

        Assert.True(result);
    }

    [Fact]
    public void IsEmbeddingDefault_FalseForRemote()
    {
        var result = this.resolver.IsEmbeddingDefault("http://mac:11434");

        Assert.False(result);
    }
}
