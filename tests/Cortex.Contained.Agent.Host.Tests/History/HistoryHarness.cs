using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>
/// A temp-directory <see cref="MessageStore"/> plus everything the projector and recall tools
/// need. <see cref="NewProjector"/> creates a fresh projector over the same files (a "restart").
/// </summary>
internal sealed class HistoryHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    public HistoryHarness(HistoryOptions? options = null, IHistoryEmbedder? embedder = null)
    {
        this.Embedder = embedder;
        this.Root = Path.Combine(Path.GetTempPath(), $"history-harness-{Guid.NewGuid():N}");
        this.Options = options ?? new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        this.Monitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
        this.Monitor.CurrentValue.Returns(_ => this.Options);
        this.Store = new MessageStore(Path.Combine(this.Root, "messages.db"), NullLogger<MessageStore>.Instance);
        this.Generations = new HistoryIndexGenerations(Path.Combine(this.Root, "history"));
        this.Projector = this.NewProjector();
    }

    public string Root { get; }

    public HistoryOptions Options { get; set; }

    public IOptionsMonitor<HistoryOptions> Monitor { get; }

    public MessageStore Store { get; }

    public HistoryIndexGenerations Generations { get; }

    public HistoryAccessGate Gate { get; } = new();

    public HistoryStatus Status { get; } = new();

    public HistoryProjector Projector { get; private set; }

    /// <summary>The embedder handed to every projector, or null (dense retrieval without vectors).</summary>
    public IHistoryEmbedder? Embedder { get; }

    /// <summary>Clock of the projector (embedding debounce).</summary>
    public FakeTimeProvider Time { get; } = new(T0);

    /// <summary>Simulates a process restart: disposes the previous projector, then creates a new one.</summary>
    public HistoryProjector NewProjector()
    {
        this.Projector?.Dispose();
        var projector = new HistoryProjector(this.Store, this.Store, this.Generations, this.Gate, this.Monitor, this.Status, NullLogger<HistoryProjector>.Instance, this.Embedder, this.Time);
        this.Projector = projector;
        return projector;
    }

    /// <summary>Initializes and, when needed, builds; then applies the journal.</summary>
    public async Task CatchUpAsync()
    {
        if (this.Projector.NeedsRebuild)
        {
            await this.Projector.RebuildAsync(CancellationToken.None);
        }

        await this.Projector.ApplyPendingAsync(CancellationToken.None);
    }

    public Task<long> SayAsync(string text, int minute, string channel = "ch", string role = "user",
        Contracts.Hub.MessageCategory category = Contracts.Hub.MessageCategory.Normal, string? toolCalls = null) =>
        this.Store.SaveMessageAsync(role == "user" ? "u" : "assistant", channel, role, text, T0.AddMinutes(minute), category: category, toolCalls: toolCalls);

    public async ValueTask DisposeAsync()
    {
        this.Projector.Dispose();
        await this.Store.DisposeAsync();
        try { Directory.Delete(this.Root, recursive: true); } catch { /* best effort */ }
    }
}
