namespace Cortex.Contained.Speech.SpeakerId;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// ONNX-Runtime backed <see cref="ISpeakerEmbedder"/>. Accepts raw PCM and
/// runs fbank feature extraction internally before ORT inference.
/// </summary>
/// <remarks>
/// Model contract:
/// <list type="bullet">
///   <item>Input: name per <c>inputName</c> ctor argument, shape <c>[1, time, 80]</c>, dtype float32.</item>
///   <item>Output: name per <c>outputName</c> ctor argument, shape <c>[1, EmbeddingDim]</c>, dtype float32.</item>
/// </list>
/// The output is L2-normalised by this wrapper before being returned, even
/// if the model already produces unit vectors.
/// </remarks>
public sealed class OnnxSpeakerEmbedder : ISpeakerEmbedder, IDisposable
{
    private const int NumMelBins = 80;

    private readonly InferenceSession session;
    private readonly SessionOptions sessionOptions;
    private readonly FbankExtractor fbank;
    private readonly string inputName;
    private readonly string outputName;
    private bool disposed;

    public string ModelId { get; }

    public int EmbeddingDim { get; }

    public OnnxSpeakerEmbedder(string modelPath, string modelId, int embeddingDim, string inputName, string outputName, FbankExtractor fbank)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Speaker-ID ONNX model not found.", modelPath);
        }

        this.sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        this.session = new InferenceSession(modelPath, this.sessionOptions);
        this.fbank = fbank;
        this.ModelId = modelId;
        this.EmbeddingDim = embeddingDim;
        this.inputName = inputName;
        this.outputName = outputName;
    }

    public ValueTask<float[]> EmbedAsync(ReadOnlyMemory<short> pcm16Mono16k, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var (frames, numFrames) = this.fbank.Extract(pcm16Mono16k.Span);
        if (numFrames == 0)
        {
            return new ValueTask<float[]>(Array.Empty<float>());
        }

        var framesArray = frames.ToArray();
        var captured = numFrames;

        // ONNX Runtime's Run() is synchronous CPU work (~30–50 ms for ERes2NetV2
        // on a typical 1–3 s clip). Offload to the thread pool so callers that
        // await this in parallel with another async operation (e.g. STT in the
        // Discord voice handler) actually get parallelism, not serial execution
        // on the calling thread.
        return new ValueTask<float[]>(Task.Run(
            () =>
            {
                var tensor = new DenseTensor<float>(framesArray, [1, captured, NumMelBins]);
                var inputs = new[] { NamedOnnxValue.CreateFromTensor(this.inputName, tensor) };

                using var results = this.session.Run(inputs);
                var output = results.First(r => r.Name == this.outputName).AsTensor<float>().ToArray();

                return SpeakerEmbeddingMath.L2Normalise(output);
            },
            cancellationToken));
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.session.Dispose();
        this.sessionOptions.Dispose();
        this.disposed = true;
    }
}
