namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

public sealed class OnnxSpeakerEmbedderTests
{
    [Fact]
    public void Constructor_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new OnnxSpeakerEmbedder(
                modelPath: "does-not-exist.onnx",
                modelId: "x",
                embeddingDim: 192,
                inputName: "feats",
                outputName: "embed",
                fbank: new FbankExtractor()));
    }
}
