using System.Text;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Evaluation;

/// <summary>What one evaluation command gets: its arguments, where to print, the protection and an optional package passphrase.</summary>
public sealed record EvaluationRun(IReadOnlyList<string> Args, TextWriter Output, EvalProtection Protection, string? Passphrase);

/// <summary>One evaluation command run inside the agent process; returns the exit code.</summary>
public delegate Task<int> EvaluationCommand(EvaluationRun run, CancellationToken cancellationToken);

/// <summary>
/// Runs the evaluation commands inside the agent process, where the storage key lives (the in-container CLI
/// processes never get it): one job at a time, in the background, with the output (ids, numbers and paths only)
/// kept for the Bridge to poll. On an encrypted install every job runs in encrypted-workspace mode
/// (<see cref="EvalProtection.Encrypted"/>). Options that would point a job at other files or endpoints than the
/// configured ones are refused.
/// </summary>
public sealed partial class EvaluationJobService : IDisposable
{
    /// <summary>Most characters of output kept per job.</summary>
    public const int MaxOutputChars = 64 * 1024;

    private const int KeptJobs = 20;

    private static readonly string[] RefusedOptions = ["--dir", "--source", "--labels", "--embedder", "--reranker"];

    private readonly IReadOnlyDictionary<string, EvaluationCommand> commands;
    private readonly Func<EvalProtection> protection;
    private readonly TimeProvider time;
    private readonly ILogger<EvaluationJobService> logger;
    private readonly Lock sync = new();
    private readonly List<Job> jobs = [];

    /// <param name="commands">Command per tool name.</param>
    /// <param name="protection">The protection of the next job; throws <see cref="StorageLockedException"/> while the key is missing.</param>
    public EvaluationJobService(IReadOnlyDictionary<string, EvaluationCommand> commands, Func<EvalProtection> protection, TimeProvider time, ILogger<EvaluationJobService> logger)
    {
        this.commands = commands;
        this.protection = protection;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>The protection for this agent: encrypted when encryption at rest is on.</summary>
    public static Func<EvalProtection> ProtectionFor(StorageEncryptionContext context, StorageKeyRing keys) =>
        () => context.Enabled ? EvalProtection.Encrypted(keys) : EvalProtection.Plaintext;

    /// <summary>Starts <paramref name="request"/> in the background, or returns a refused job.</summary>
    public EvaluationJobDto Start(EvaluationStartRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = this.time.GetUtcNow();
        var command = request.Args.Count > 0 ? request.Args[0] : null;
        if (!this.commands.TryGetValue(request.Tool, out var run))
        {
            return Refused(request.Tool, command, now, $"Unknown tool '{request.Tool}': use {string.Join(", ", this.commands.Keys.Order(StringComparer.Ordinal))}.");
        }

        if (request.Args.FirstOrDefault(a => RefusedOptions.Contains(a, StringComparer.Ordinal)) is { } option)
        {
            return Refused(request.Tool, command, now, $"{option} is not available for jobs: they use the configured workspace, stores and endpoints.");
        }

        EvalProtection jobProtection;
        try
        {
            jobProtection = this.protection();
        }
        catch (StorageLockedException)
        {
            return Refused(request.Tool, command, now, "The storage key has not arrived from the Bridge yet: the encrypted stores cannot be read.");
        }

        lock (this.sync)
        {
            if (this.jobs.Any(j => j.State == EvaluationJobStates.Running))
            {
                return Refused(request.Tool, command, now, "Another evaluation job is running; wait for it or cancel it.");
            }

            var job = new Job(Guid.NewGuid().ToString("N")[..12], request.Tool, command, jobProtection.IsEncrypted, now);
            this.jobs.Add(job);
            if (this.jobs.Count > KeptJobs)
            {
                this.jobs.RemoveAt(this.jobs.FindIndex(j => j.State != EvaluationJobStates.Running));
            }

            this.LogStarted(job.Id, request.Tool, command ?? string.Empty, job.Encrypted);
            job.Task = Task.Run(() => this.RunAsync(job, run, new EvaluationRun(request.Args, job.Output, jobProtection, request.Passphrase)), CancellationToken.None);
            return job.ToDto();
        }
    }

    /// <summary>The job, or null.</summary>
    public EvaluationJobDto? Get(string jobId)
    {
        lock (this.sync)
        {
            return this.jobs.FirstOrDefault(j => string.Equals(j.Id, jobId, StringComparison.Ordinal))?.ToDto();
        }
    }

    /// <summary>Recent jobs, newest first.</summary>
    public IReadOnlyList<EvaluationJobDto> List()
    {
        lock (this.sync)
        {
            return this.jobs.AsEnumerable().Reverse().Select(j => j.ToDto()).ToList();
        }
    }

    /// <summary>Requests cancellation of a running job.</summary>
    public bool Cancel(string jobId)
    {
        lock (this.sync)
        {
            var job = this.jobs.FirstOrDefault(j => string.Equals(j.Id, jobId, StringComparison.Ordinal));
            if (job is not { State: EvaluationJobStates.Running })
            {
                return false;
            }

            job.Cancellation.Cancel();
            return true;
        }
    }

    /// <summary>Waits for the running job, if any (tests and shutdown).</summary>
    public Task WhenIdleAsync()
    {
        lock (this.sync)
        {
            return Task.WhenAll(this.jobs.Select(j => j.Task));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this.sync)
        {
            foreach (var job in this.jobs)
            {
                job.Cancellation.Cancel();
                job.Cancellation.Dispose();
            }
        }
    }

    private static EvaluationJobDto Refused(string tool, string? command, DateTimeOffset now, string reason) => new()
    {
        Id = string.Empty,
        Tool = tool,
        Command = command,
        State = EvaluationJobStates.Refused,
        StartedAt = now,
        FinishedAt = now,
        Output = reason,
    };

    private async Task RunAsync(Job job, EvaluationCommand run, EvaluationRun arguments)
    {
        string state;
        int? exitCode = null;
        try
        {
            exitCode = await run(arguments, job.Cancellation.Token).ConfigureAwait(false);
            state = exitCode == 0 ? EvaluationJobStates.Succeeded : EvaluationJobStates.Failed;
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            state = EvaluationJobStates.Cancelled;
        }
#pragma warning disable CA1031 // A failing command ends its job, never the agent; only the exception type is reported (messages may hold text).
        catch (Exception ex)
        {
            await job.Output.WriteLineAsync("error: " + ex.GetType().Name).ConfigureAwait(false);
            state = EvaluationJobStates.Failed;
        }
#pragma warning restore CA1031

        lock (this.sync)
        {
            job.State = state;
            job.ExitCode = exitCode;
            job.FinishedAt = this.time.GetUtcNow();
        }

        this.LogFinished(job.Id, state, exitCode ?? -1);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation job {JobId} started: {Tool} {Command} (encrypted workspace: {Encrypted})")]
    private partial void LogStarted(string jobId, string tool, string command, bool encrypted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation job {JobId} finished: {State} (exit code {ExitCode})")]
    private partial void LogFinished(string jobId, string state, int exitCode);

    private sealed class Job(string id, string tool, string? command, bool encrypted, DateTimeOffset startedAt)
    {
        public string Id { get; } = id;

        public bool Encrypted { get; } = encrypted;

        public JobOutput Output { get; } = new();

        public CancellationTokenSource Cancellation { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;

        public string State { get; set; } = EvaluationJobStates.Running;

        public int? ExitCode { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }

        public EvaluationJobDto ToDto() => new()
        {
            Id = this.Id,
            Tool = tool,
            Command = command,
            State = this.State,
            ExitCode = this.ExitCode,
            Encrypted = this.Encrypted,
            StartedAt = startedAt,
            FinishedAt = this.FinishedAt,
            Output = this.Output.Snapshot(),
        };
    }

    /// <summary>A thread-safe, capped text sink the Bridge can read while the job runs.</summary>
    private sealed class JobOutput : TextWriter
    {
        private readonly StringBuilder text = new();
        private readonly Lock gate = new();
        private bool truncated;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => this.Append(value.ToString());

        public override void Write(char[] buffer, int index, int count) => this.Append(new string(buffer, index, count));

        public override void Write(string? value) => this.Append(value);

        public string Snapshot()
        {
            lock (this.gate)
            {
                return this.truncated ? this.text + "\n[output truncated]" : this.text.ToString();
            }
        }

        private void Append(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (this.gate)
            {
                var room = MaxOutputChars - this.text.Length;
                if (value.Length <= room)
                {
                    this.text.Append(value);
                    return;
                }

                this.text.Append(value.AsSpan(0, Math.Max(0, room)));
                this.truncated = true;
            }
        }
    }
}
