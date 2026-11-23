using Cortex.Contained.Agent.Host.Evaluation.Sanitising;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Sanitising;

public sealed class LexiconSuggesterTests
{
    private static readonly SanitiserLexicon Known = SanitiserLexicon.Parse(["""{"term":"Alvenia","kind":"person"}"""]);

    [Fact]
    public void Capitalised_ProperNounsNotSentenceInitialAndNeverLowercase_Suggested()
    {
        string[] texts =
        [
            "We met Dorvin at the market. Then Dorvin left.",
            "Ask Dorvin and Alvenia about Morvenby tomorrow",
            "the train goes to Morvenby. Train tickets are cheap, the train is late",
        ];

        var suggestions = LexiconSuggester.Capitalised(texts, Known);

        Assert.Equal(["Dorvin", "Morvenby"], suggestions.Select(s => s.Term));
        Assert.All(suggestions, s => Assert.Equal("capitalised", s.Source));
    }

    [Fact]
    public async Task FromLlmAsync_ParsesEntitiesSkipsKnownTermsAndUnknownKinds()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>()).Returns(new LlmCompletionResult
        {
            Success = true,
            Content = "```json\n" + """[{"term":"Dorvin","kind":"person"},{"term":"Alvenia","kind":"person"},{"term":"Morvenby","kind":"place"},{"term":"Rex","kind":"pet"}]""" + "\n```",
        });

        var suggestions = await LexiconSuggester.FromLlmAsync(["Dorvin and Alvenia went to Morvenby with Rex"], Known, llm, "model-a", maxBatches: 5, CancellationToken.None);

        Assert.Equal(["Dorvin:person", "Morvenby:place"], suggestions.Select(s => s.Term + ":" + s.Kind));
        await llm.Received(1).CompleteAsync(Arg.Is<LlmCompletionRequest>(r => r.Model == "model-a" && r.Temperature == 0), Arg.Any<CancellationToken>());
    }
}
