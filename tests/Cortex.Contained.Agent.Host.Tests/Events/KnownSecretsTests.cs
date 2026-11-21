using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Events;

public sealed class KnownSecretsTests
{
    [Fact]
    public void Load_CollectsEveryProviderSecretAndMarksLoaded()
    {
        var secrets = new KnownSecrets();
        Assert.False(secrets.IsLoaded);

        secrets.Load(new LlmCredentials
        {
            Providers =
            [
                new LlmProviderCredential { Name = "a", Api = "openai-completions", ApiKey = "key-aaaaaaaa", Models = [] },
                new LlmProviderCredential { Name = "b", Api = "anthropic-messages", AccessToken = "tok-bbbbbbbb", RefreshToken = "ref-cccccccc", Models = [] },
                new LlmProviderCredential { Name = "c", Api = "openai-completions", ApiKey = "key-aaaaaaaa", Models = [] },
            ],
        });

        Assert.True(secrets.IsLoaded);
        Assert.Equal(["key-aaaaaaaa", "tok-bbbbbbbb", "ref-cccccccc"], secrets.Values);
    }
}
