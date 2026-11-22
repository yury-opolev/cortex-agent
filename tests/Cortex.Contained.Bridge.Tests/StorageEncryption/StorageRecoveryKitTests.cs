using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

public sealed class StorageRecoveryKitTests
{
    private const int FastIterations = 1_000;
    private const string Passphrase = "correct horse battery staple";
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly DateTimeOffset At = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static string Kit(int iterations = FastIterations) =>
        Encoding.UTF8.GetString(StorageRecoveryKit.Create("default", Key, Passphrase, At, iterations));

    [Fact]
    public void CreateThenOpen_RoundTripsTheKeyAndHoldsNoKeyMaterial()
    {
        var kit = Kit();

        var recovered = StorageRecoveryKit.Open(kit, Passphrase, minimumIterations: FastIterations);

        Assert.Equal(new RecoveredStorageKey("default", Key, StorageKeyFingerprint.Compute(Key)), recovered);
        Assert.DoesNotContain(Key, kit, StringComparison.Ordinal);
        Assert.Contains(StorageRecoveryKit.Format, kit, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_WrongPassphrase_Refused()
    {
        var ex = Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open(Kit(), "wrong passphrase!", minimumIterations: FastIterations));

        Assert.StartsWith("Wrong passphrase", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tenantId", "other")]
    [InlineData("keyFingerprint", "0000000000000000")]
    public void Open_RelabelledKit_Refused(string field, string value)
    {
        var node = JsonNode.Parse(Kit())!;
        node[field] = value;

        Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open(node.ToJsonString(), Passphrase, minimumIterations: FastIterations));
    }

    [Fact]
    public void Open_TooFewIterationsOrNotAKit_Refused()
    {
        Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open(Kit(), Passphrase));
        Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open("{\"format\":\"x\"}", Passphrase));
        Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open("not json", Passphrase));
    }

    [Fact]
    public void Create_ShortPassphrase_Refused()
    {
        Assert.Throws<ArgumentException>(() => StorageRecoveryKit.Create("default", Key, "short", At, FastIterations));
    }

    [Fact]
    public void Create_DefaultsToTheStrongKdf()
    {
        using var kit = JsonDocument.Parse(StorageRecoveryKit.Create("default", Key, Passphrase, At));

        Assert.Equal(StorageRecoveryKit.DefaultIterations, kit.RootElement.GetProperty("kdf").GetProperty("iterations").GetInt32());
        Assert.Equal(Key, StorageRecoveryKit.Open(kit.RootElement.GetRawText(), Passphrase).Base64Key);
    }
}
