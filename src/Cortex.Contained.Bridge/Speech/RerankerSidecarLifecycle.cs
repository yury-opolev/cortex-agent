using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>What the Bridge knows about the cortex-reranker sidecar after its last reconcile.</summary>
/// <param name="State"><c>unknown</c> (never reconciled), <c>starting</c>, <c>running</c>, <c>stopped</c> or <c>failed</c>.</param>
/// <param name="Problem">Why it is not running when it should be, in words the owner can act on; null otherwise.</param>
/// <param name="At">When the state was recorded, or null.</param>
public sealed record RerankerSidecarStatus(string State, string? Problem, DateTimeOffset? At)
{
    /// <summary>Never reconciled.</summary>
    public static RerankerSidecarStatus Unknown { get; } = new("unknown", null, null);
}

/// <summary>
/// Converges the cortex-reranker sidecar with <c>Reranker:Enabled</c>: when enabled it always runs
/// `compose up` with the DPAPI-held API key (idempotent; compose recreates the container after a key
/// or image change, which is how a Self-Update image upgrade or rollback reaches the sidecar); when
/// disabled it stops a running sidecar. Failures are logged and reported in <see cref="Status"/>
/// (missing image, compose failure such as no GPU, a container that exits at once), never propagated.
/// </summary>
public sealed partial class RerankerSidecarLifecycle
{
    /// <summary>Shown when <c>cortex-reranker:latest</c> does not exist (compose never pulls it).</summary>
    public const string MissingImageProblem =
        "The cortex-reranker image is not built on this machine. Build it with scripts\\Build-RerankerImage.ps1 " +
        "(or Build-All.ps1 -WithReranker), then switch the reranker on again.";

    /// <summary>Shown when compose could not start the container.</summary>
    public const string StartFailedProblem =
        "docker compose could not start cortex-reranker. The default CUDA image needs an NVIDIA GPU with Docker GPU " +
        "support; without one, build the CPU variant (scripts\\Build-RerankerImage.ps1 -Variant cpu). Details are in the Bridge log.";

    /// <summary>Shown when the container exited right after starting.</summary>
    public const string NotRunningProblem =
        "cortex-reranker started but is not running (it may have exited: GPU unavailable or model load failure). " +
        "Check `docker logs cortex-reranker`.";

    private readonly IRerankerComposeRunner runner;
    private readonly Func<string> apiKey;
    private readonly ILogger<RerankerSidecarLifecycle> logger;
    private readonly TimeProvider time;
    private volatile RerankerSidecarStatus status = RerankerSidecarStatus.Unknown;

    public RerankerSidecarLifecycle(IRerankerComposeRunner runner, Func<string> apiKey, ILogger<RerankerSidecarLifecycle> logger, TimeProvider? time = null)
    {
        this.runner = runner;
        this.apiKey = apiKey;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The state recorded by the last reconcile.</summary>
    public RerankerSidecarStatus Status => this.status;

    /// <summary>Starts or stops the sidecar to match <paramref name="enabled"/>; returns (and records) the outcome.</summary>
    public async Task<RerankerSidecarStatus> ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            if (enabled)
            {
                this.Record("starting", null);
                this.LogConverging();
                if (!await this.runner.HasRerankerImageAsync(cancellationToken).ConfigureAwait(false))
                {
                    this.LogStartFailed();
                    return this.Record("failed", MissingImageProblem);
                }

                if (!await this.runner.StartRerankerAsync(this.apiKey(), cancellationToken).ConfigureAwait(false))
                {
                    this.LogStartFailed();
                    return this.Record("failed", StartFailedProblem);
                }

                return await this.runner.IsRerankerRunningAsync(cancellationToken).ConfigureAwait(false)
                    ? this.Record("running", null)
                    : this.Record("failed", NotRunningProblem);
            }

            if (await this.runner.IsRerankerRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                this.LogStopping();
                await this.runner.StopRerankerAsync(cancellationToken).ConfigureAwait(false);
            }

            return this.Record("stopped", null);
        }
#pragma warning disable CA1031 // A sidecar problem must never take the Bridge down
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
            return this.Record("failed", "The reranker could not be reconciled: " + ex.Message);
        }
#pragma warning restore CA1031
    }

    private RerankerSidecarStatus Record(string state, string? problem)
    {
        var next = new RerankerSidecarStatus(state, problem, this.time.GetUtcNow());
        this.status = next;
        return next;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "reranker enabled — converging cortex-reranker (compose up)")]
    private partial void LogConverging();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-reranker did not start; searches keep the fused order until it does")]
    private partial void LogStartFailed();

    [LoggerMessage(Level = LogLevel.Information, Message = "reranker disabled — stopping cortex-reranker")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-reranker reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
