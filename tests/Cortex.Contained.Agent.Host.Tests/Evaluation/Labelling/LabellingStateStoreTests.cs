using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class LabellingStateStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"label-state-{Guid.NewGuid():N}");

    public LabellingStateStoreTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    [Fact]
    public void Load_NothingSaved_Null() => Assert.Null(new LabellingStateStore(this.root, EvalProtection.Plaintext).Load());

    [Fact]
    public void Save_Encrypted_SealedAndRoundTrips()
    {
        var store = new LabellingStateStore(this.root, EvalProtection.Encrypted(EvalTestKeys.Ring()));
        var state = new LabellingState { Tool = "history-eval", Calls = 7 };
        state.Seeds.Add(new LabelSeed { SeedId = "s0001", Category = "temporal", SourceIds = ["42"] });
        state.Drafts["s0001"] = new DraftedQuery
        {
            Id = "h0001", SeedId = "s0001", Text = "zqsecret when was the ferry booked", Category = "temporal",
            GoldFilters = new EvalFilters { Since = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) },
        };
        state.Grades["h0001"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["42"] = 2 };

        store.Save(state);
        var loaded = store.Load()!;

        Assert.True(EvalProtection.IsSealed(store.Path));
        Assert.DoesNotContain("zqsecret", File.ReadAllText(store.Path), StringComparison.Ordinal);
        Assert.Equal(7, loaded.Calls);
        Assert.Equal("zqsecret when was the ferry booked", loaded.Drafts["s0001"].Text);
        Assert.Equal(2, loaded.Grades["h0001"]["42"]);
        Assert.Equal(["42"], loaded.Seeds[0].SourceIds);
    }
}
