using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class QueryDrafterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 14, 18, 0, 0, TimeSpan.Zero);

    private static readonly SeedMaterial Snippet = new(
    [
        new MaterialItem("42", "we booked the night ferry to TownB for the fourteenth of March", "discord-dm", T0, "user"),
    ]);

    private readonly FakeLabellingLlm fake = new();

    private QueryDrafter Drafter(string tool = "history-eval") =>
        new(new LabellingLlm(this.fake, FakeLabellingLlm.ServesAll, new LabellingOptions { MinCallIntervalMs = 0 }, delay: (_, _) => Task.CompletedTask), "gpt-6.1-sol", tool, 3, 1500);

    private static LabelSeed Seed(string category) => new() { SeedId = "s0001", Category = category, SourceIds = ["42"] };

    [Fact]
    public async Task DraftAsync_CopiedPhraseThenGuardFailure_WritesAgainWithTheReason()
    {
        var replies = new Queue<string>(
        [
            """{"text":"when is the night ferry to TownB","category":"paraphrase"}""",
            """{"text":"which boat trip did we reserve","category":"paraphrase"}""",
            """{"text":"what overnight sea crossing did I arrange","category":"paraphrase"}""",
        ]);
        this.fake.Respond = (_, _, _) => replies.Dequeue();

        var answer = await this.Drafter().DraftAsync(Seed("paraphrase"), Snippet, (text, _) => !text.Contains("boat", StringComparison.Ordinal), CancellationToken.None);

        Assert.Equal("what overnight sea crossing did I arrange", answer!.Text);
        Assert.Equal(3, this.fake.Requests.Count);
        Assert.All(this.fake.Requests, r => Assert.Equal("gpt-6.1-sol", r.Model));
        Assert.Contains("Kind of query: paraphrase", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("Current time (UTC): ", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("night ferry to TownB", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("four or more consecutive words", this.fake.Requests[1].User, StringComparison.Ordinal);
        Assert.Contains("paraphrase guard", this.fake.Requests[2].User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DraftAsync_TemporalFilterMissesTheMessageTime_Rejected()
    {
        this.fake.Respond = (_, _, _) => """{"text":"what did we plan in February","category":"temporal","goldFilters":{"since":"2026-02-01T00:00:00Z","until":"2026-03-01T00:00:00Z"}}""";

        var answer = await this.Drafter().DraftAsync(Seed("temporal"), Snippet, (_, _) => true, CancellationToken.None);

        Assert.Null(answer);
        Assert.Equal(3, this.fake.Requests.Count);
    }

    [Fact]
    public async Task DraftAsync_TemporalFilterContainsTheTime_KeepsFilters()
    {
        this.fake.Respond = (_, _, _) => """{"text":"what did we plan in mid March","category":"lookup","goldFilters":{"since":"2026-03-01T00:00:00Z","until":"2026-04-01T00:00:00Z"}}""";

        var answer = await this.Drafter().DraftAsync(Seed("temporal"), Snippet, (_, _) => true, CancellationToken.None);

        Assert.Equal("temporal", answer!.Category);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), answer.GoldFilters!.Until);
    }

    [Fact]
    public async Task DraftAsync_RealRequest_ShowsRequestAndContextAndClampsTheCategory()
    {
        this.fake.Respond = (_, _, _) => """{"text":"when did we book the ferry to TownB","category":"abstention"}""";
        var seed = new LabelSeed { SeedId = "s0002", Category = "real", Stratum = "real", RequestTime = T0 };
        var material = new SeedMaterial([], "and when did we book it?") { Context = ["user: about the ferry to TownB"] };

        var answer = await this.Drafter().DraftAsync(seed, material, (_, _) => true, CancellationToken.None);

        Assert.Equal("lookup", answer!.Category);
        var prompt = Assert.Single(this.fake.Requests).User;
        Assert.Contains("and when did we book it?", prompt, StringComparison.Ordinal);
        Assert.Contains("user: about the ferry to TownB", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Snippet:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DraftAsync_TagFilteredMemory_TagsMustBeTheMemorysTags()
    {
        var replies = new Queue<string>(
        [
            """{"text":"which invoices did I file","category":"tag_filtered","tags":["receipts"]}""",
            """{"text":"which invoices did I file","category":"tag_filtered","tags":["Finance"]}""",
        ]);
        this.fake.Respond = (_, _, _) => replies.Dequeue();
        var material = new SeedMaterial([new MaterialItem("mem-1", "Invoice for the boat repair was paid", Title: "Invoice") { Tags = ["finance"] }]);

        var answer = await this.Drafter("memory-eval").DraftAsync(Seed("tag_filtered"), material, (_, _) => true, CancellationToken.None);

        Assert.Equal(["Finance"], answer!.Tags);
        Assert.Contains("tags: finance", this.fake.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("curated memories", this.fake.Requests[0].System, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("night ferry to TownB", 5, false)]
    [InlineData("the night ferry to townb", 4, true)]
    [InlineData("booked the night", 3, true)]
    public void SharesRun_FoldedWords(string query, int run, bool expected) =>
        Assert.Equal(expected, DraftChecks.SharesRun(query, "We booked the NIGHT ferry to TownB.", run));

    [Fact]
    public void Problem_IdentifierWithoutIdentifier_Rejected()
    {
        var answer = new DraftAnswer { Text = "which invoice was paid", Category = "identifier" };

        Assert.Contains("identifier", DraftChecks.Problem(Seed("identifier"), Snippet, answer, (_, _) => true), StringComparison.Ordinal);
        Assert.Null(DraftChecks.Problem(Seed("identifier"), Snippet, answer with { Text = "was INV-2041 paid" }, (_, _) => true));
        Assert.NotNull(DraftChecks.Problem(Seed("lookup"), Snippet, answer with { Text = string.Empty }, (_, _) => true));
    }
}
