using Cortex.Contained.Bridge.Connectors.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class ConnectorTokenStoreTests
{
    [Fact]
    public void GetAll_Empty_ReturnsEmptyList()
    {
        var (store, _) = BuildStore();

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Save_NewRecord_CanBeRetrievedWithGet()
    {
        var (store, _) = BuildStore();
        var record = MakeRecord();

        store.Save(record);

        var loaded = store.Get(record.ChannelId);
        Assert.NotNull(loaded);
        Assert.Equal(record.ChannelId, loaded.ChannelId);
        Assert.Equal(record.Token, loaded.Token);
        Assert.True(loaded.Enabled);
    }

    [Fact]
    public void GetAll_AfterSave_ContainsRecord()
    {
        var (store, _) = BuildStore();

        store.Save(MakeRecord());

        Assert.Single(store.GetAll());
    }

    [Fact]
    public void Remove_ExistingRecord_ReturnsTrue()
    {
        var (store, _) = BuildStore();
        store.Save(MakeRecord());

        Assert.True(store.Remove("plugin:terminal:default"));
        Assert.Null(store.Get("plugin:terminal:default"));
    }

    [Fact]
    public void Remove_NonExistentRecord_ReturnsFalse()
    {
        var (store, _) = BuildStore();

        Assert.False(store.Remove("plugin:terminal:default"));
    }

    [Fact]
    public void UpdateLastSeen_ExistingRecord_UpdatesTimestamp()
    {
        var (store, _) = BuildStore();
        store.Save(MakeRecord());
        var now = DateTimeOffset.UtcNow;

        store.UpdateLastSeen("plugin:terminal:default", now);

        var updated = store.Get("plugin:terminal:default");
        Assert.Equal(now, updated!.LastSeenAt);
    }

    [Fact]
    public void UpdateLastSeen_NonExistentRecord_IsNoOp()
    {
        var (store, backing) = BuildStore();

        store.UpdateLastSeen("plugin:terminal:default", DateTimeOffset.UtcNow);

        Assert.Empty(backing);
    }

    [Fact]
    public void SetEnabled_ExistingRecord_TogglesFlag()
    {
        var (store, _) = BuildStore();
        store.Save(MakeRecord());

        Assert.True(store.SetEnabled("plugin:terminal:default", false));

        var updated = store.Get("plugin:terminal:default");
        Assert.False(updated!.Enabled);
    }

    [Fact]
    public void SetEnabled_SameValue_ReturnsFalse()
    {
        var (store, _) = BuildStore();
        store.Save(MakeRecord());

        Assert.False(store.SetEnabled("plugin:terminal:default", true));
    }

    [Fact]
    public void SetEnabled_NonExistentRecord_ReturnsFalse()
    {
        var (store, _) = BuildStore();

        Assert.False(store.SetEnabled("plugin:terminal:default", false));
    }

    [Fact]
    public void Get_CorruptBlob_ReturnsNullAndDoesNotThrow()
    {
        var (store, backing) = BuildStore();
        backing[ConnectorTokenStore.SecretId] = "not-valid-json{{{{";

        Assert.Null(store.Get("any"));
    }

    [Fact]
    public void GetAll_CorruptBlob_ReturnsEmptyAndDoesNotThrow()
    {
        var (store, backing) = BuildStore();
        backing[ConnectorTokenStore.SecretId] = "not-valid-json{{{{";

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Save_MultipleRecords_AllPersistedInOneSecret()
    {
        var (store, backing) = BuildStore();

        store.Save(MakeRecord());
        store.Save(MakeRecord("plugin:terminal:second") with { InstanceId = "second" });

        Assert.Equal(2, store.GetAll().Count);
        Assert.Single(backing);
        Assert.True(backing.ContainsKey(ConnectorTokenStore.SecretId));
    }

    private static (ConnectorTokenStore Store, Dictionary<string, string> Backing) BuildStore()
    {
        var backing = new Dictionary<string, string>(StringComparer.Ordinal);
        var fake = new FakeConnectorSecretStore(backing);
        var store = new ConnectorTokenStore(fake, NullLogger<ConnectorTokenStore>.Instance);
        return (store, backing);
    }

    private static ConnectorRecord MakeRecord(string channelId = "plugin:terminal:default") => new()
    {
        ChannelId = channelId,
        Key = "terminal",
        InstanceId = "default",
        DisplayName = "Terminal",
        Token = "tok-abc",
        PairedAt = DateTimeOffset.UtcNow,
    };
}
