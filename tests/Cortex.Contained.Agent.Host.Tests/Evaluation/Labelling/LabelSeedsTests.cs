using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Memory.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

public sealed class LabelSeedsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private static List<SourceMessage> Messages()
    {
        var messages = new List<SourceMessage>();
        for (var i = 0; i < 240; i++)
        {
            var channel = i % 2 == 0 ? "webchat-default" : "discord-dm";
            var text = i % 9 == 0
                ? $"the invoice INV-{2000 + i} for the boat repair in TownB was paid by transfer"
                : $"we talked about topic number {i} and what my friend thinks of the plan for the weekend";
            messages.Add(new SourceMessage(i + 1, channel, i % 3 == 0 ? "assistant" : "user", T0.AddHours(i * 9), text));
        }

        messages.Add(new SourceMessage(1000, "webchat-default", "user", T0, "too short"));
        messages.Add(new SourceMessage(1001, "webchat-default", "tool", T0, "a tool result that is long enough to be eligible otherwise"));
        return messages;
    }

    [Fact]
    public void History_SyntheticOnly_FillsTheQuotasWithFittingDistinctSources()
    {
        var messages = Messages();
        var byId = messages.ToDictionary(m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var seeds = LabelSeeds.History(messages, [], 50, realShare: 0.3);

        Assert.Equal(50, seeds.Count);
        var counts = seeds.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(11, counts["lookup"]);
        Assert.Equal(10, counts["paraphrase"]);
        Assert.Equal(5, counts["cross_language"]);
        Assert.Equal(7, counts["temporal"]);
        Assert.Equal(5, counts["identifier"]);
        Assert.Equal(7, counts["enumeration"]);
        Assert.Equal(5, counts["abstention"]);
        Assert.All(seeds.Where(s => s.Category == "identifier"), s => Assert.Contains("INV-", byId[s.SourceIds[0]].Content, StringComparison.Ordinal));
        Assert.All(seeds.Where(s => s.Category == "enumeration"), s =>
        {
            Assert.InRange(s.SourceIds.Count, 3, 5);
            Assert.Single(s.SourceIds.Select(id => byId[id].ChannelId).Distinct());
        });
        Assert.All(seeds, s => Assert.Equal(s.Category != "abstention", s.SourceIsGold));
        var sources = seeds.SelectMany(s => s.SourceIds).ToList();
        Assert.Equal(sources.Count, sources.Distinct().Count());
        Assert.DoesNotContain("1000", sources);
        Assert.DoesNotContain("1001", sources);
        Assert.Equal("s0001", seeds[0].SeedId);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(seeds),
            System.Text.Json.JsonSerializer.Serialize(LabelSeeds.History(messages, [], 50, realShare: 0.3)));
    }

    [Fact]
    public void History_RealRequests_TakeTheirShareFirstAcrossMonths()
    {
        var real = Enumerable.Range(0, 10)
            .Select(i => new LabelSeed { SeedId = string.Empty, Category = "real", RequestMessageId = $"{500 + i}", RequestTime = T0.AddDays(i < 8 ? 0 : 40) })
            .ToList();

        var seeds = LabelSeeds.History(Messages(), real, 20, realShare: 0.3);

        var chosen = seeds.Take(6).ToList();
        Assert.All(chosen, s => Assert.Equal(("real", "real"), (s.Category, s.Stratum)));
        Assert.Equal(2, chosen.Count(s => s.RequestTime > T0.AddDays(30)));
        Assert.Equal(14, seeds.Count(s => s.Stratum == "synthetic"));
    }

    [Fact]
    public void Memory_MultiGetsRelatedMemoriesAndAbstentionOnlyAHint()
    {
        var memories = Enumerable.Range(0, 40)
            .Select(i => new MemorySeedSource($"mem-{i:00}", null, [i % 2 == 0 ? "travel" : "finance"], $"memory {i} about item INV-{3000 + i}"))
            .ToList();

        var seeds = LabelSeeds.Memory(memories, [], 30, realShare: 0.3);

        Assert.Equal(30, seeds.Count);
        var multi = seeds.Where(s => s.Category == "multi").ToList();
        Assert.NotEmpty(multi);
        Assert.All(multi, s => Assert.True(s.SourceIds.Count > 1));
        var abstention = seeds.Where(s => s.Category == "abstention").ToList();
        Assert.NotEmpty(abstention);
        Assert.All(abstention, s => Assert.False(s.SourceIsGold));
    }
}
