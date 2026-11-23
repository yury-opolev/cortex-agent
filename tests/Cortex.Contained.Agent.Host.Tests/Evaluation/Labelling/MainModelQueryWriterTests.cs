using Cortex.Contained.Agent.Host.Evaluation.Labelling;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class MainModelQueryWriterTests
{
    private readonly FakeLabellingLlm fake = new();

    private MainModelQueryWriter Writer() =>
        new(new LabellingLlm(this.fake, FakeLabellingLlm.ServesAll, new LabellingOptions { MinCallIntervalMs = 0 }, delay: (_, _) => Task.CompletedTask), "main-model");

    [Fact]
    public async Task WriteAsync_History_UsesTheToolDescriptionAndReadsFilters()
    {
        this.fake.Respond = (_, _, _) => """{"query":"ferry TownB","order":"relevance","since":"2026-03-01T00:00:00Z","until":null,"channel":"discord-dm"}""";

        var written = await this.Writer().WriteAsync("history-eval", "when did we book the ferry", CancellationToken.None);

        Assert.Equal("ferry TownB", written!.Text);
        Assert.Equal("discord-dm", written.Filters!.Channel);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), written.Filters.Since);
        var request = Assert.Single(this.fake.Requests);
        Assert.Equal("main-model", request.Model);
        Assert.Contains("history_search", request.System, StringComparison.Ordinal);
        Assert.Contains("when did we book the ferry", request.User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_Memory_ReadsTagsGivenAsAString()
    {
        this.fake.Respond = (_, _, _) => """{"query":"boat invoice","tags":"[\"finance\"]"}""";

        var written = await this.Writer().WriteAsync("memory-eval", "which boat invoice did I pay", CancellationToken.None);

        Assert.Equal(["finance"], written!.Tags);
        Assert.Null(written.Filters);
        Assert.Contains("memory_search", this.fake.Requests[0].System, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"query\":\"cut")]
    [InlineData("{\"order\":\"time\"}")]
    [InlineData("[1,2]")]
    public void FromArgs_NoQuery_Null(string? json) => Assert.Null(MainModelQueryWriter.FromArgs(json));
}
