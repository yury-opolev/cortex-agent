using Cortex.Contained.Agent.Host.Reranking;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The reranker sees raw best-matching lines only, within the character budget.</summary>
public sealed class RerankDocumentTests
{
    [Fact]
    public void Build_KeepsBestMatchingLinesWithinBudget_InOriginalOrder()
    {
        string[] lines = ["hello there", "the night train to TownB leaves at 22:10", "ok", "train tickets are booked"];

        var document = RerankDocument.Build("night train", lines, 66);

        Assert.Equal("the night train to TownB leaves at 22:10\ntrain tickets are booked", document);
    }

    [Fact]
    public void Build_FirstLineLongerThanBudget_IsCut()
    {
        var document = RerankDocument.Build("budget", [new string('x', 300) + " budget"], 200);

        Assert.Equal(200, document.Length);
    }

    [Fact]
    public void Build_FoldsQueryAndLines_AndFlattensLineBreaks()
    {
        string[] lines = ["unrelated", "Køreplan\nfor toget"];

        var document = RerankDocument.Build("koreplan", lines, 15);

        Assert.Equal("Køreplan for toget"[..15], document);
    }
}
