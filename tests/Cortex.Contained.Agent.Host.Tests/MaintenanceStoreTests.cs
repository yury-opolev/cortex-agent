using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for <see cref="MaintenanceStore"/>. Verifies SQLite persistence of
/// maintenance task state and run history.
/// </summary>
public class MaintenanceStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MaintenanceStore _store;

    public MaintenanceStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"maintenance-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new MaintenanceStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── State tracking ─────────────────────────────────────────────────

    [Fact]
    public void GetLastCompletedDate_WhenNeverRun_ReturnsNull()
    {
        var result = _store.GetLastCompletedDate("unknown-task");
        Assert.Null(result);
    }

    [Fact]
    public void SetLastCompletedDate_ThenGet_ReturnsDate()
    {
        var date = new DateOnly(2026, 3, 12);
        _store.SetLastCompletedDate("test-task", date);

        var result = _store.GetLastCompletedDate("test-task");
        Assert.Equal(date, result);
    }

    [Fact]
    public void SetLastCompletedDate_UpdatesExistingValue()
    {
        _store.SetLastCompletedDate("test-task", new DateOnly(2026, 3, 10));
        _store.SetLastCompletedDate("test-task", new DateOnly(2026, 3, 12));

        var result = _store.GetLastCompletedDate("test-task");
        Assert.Equal(new DateOnly(2026, 3, 12), result);
    }

    [Fact]
    public void SetLastCompletedDate_MultipleTasks_TrackedIndependently()
    {
        _store.SetLastCompletedDate("task-a", new DateOnly(2026, 3, 10));
        _store.SetLastCompletedDate("compaction", new DateOnly(2026, 3, 11));

        Assert.Equal(new DateOnly(2026, 3, 10), _store.GetLastCompletedDate("task-a"));
        Assert.Equal(new DateOnly(2026, 3, 11), _store.GetLastCompletedDate("compaction"));
    }

    // ── Run history ────────────────────────────────────────────────────

    [Fact]
    public void RecordRunStart_ReturnsPositiveRunId()
    {
        var runId = _store.RecordRunStart("test-task", new DateOnly(2026, 3, 12));
        Assert.True(runId > 0);
    }

    [Fact]
    public void RecordRunSuccess_UpdatesLastCompletedDate()
    {
        var date = new DateOnly(2026, 3, 12);
        var runId = _store.RecordRunStart("test-task", date);

        _store.RecordRunSuccess(runId, "test-task", date);

        Assert.Equal(date, _store.GetLastCompletedDate("test-task"));
    }

    [Fact]
    public void RecordRunFailure_DoesNotUpdateLastCompletedDate()
    {
        var date = new DateOnly(2026, 3, 12);
        var runId = _store.RecordRunStart("test-task", date);

        _store.RecordRunFailure(runId, "something went wrong");

        Assert.Null(_store.GetLastCompletedDate("test-task"));
    }

    [Fact]
    public void RecordRunSuccess_MultipleRuns_TracksAll()
    {
        var date1 = new DateOnly(2026, 3, 10);
        var date2 = new DateOnly(2026, 3, 11);

        var runId1 = _store.RecordRunStart("test-task", date1);
        _store.RecordRunSuccess(runId1, "test-task", date1);

        var runId2 = _store.RecordRunStart("test-task", date2);
        _store.RecordRunSuccess(runId2, "test-task", date2);

        // Should reflect the latest completed date
        Assert.Equal(date2, _store.GetLastCompletedDate("test-task"));
        // Run IDs should be unique
        Assert.NotEqual(runId1, runId2);
    }

    // ── Purge ──────────────────────────────────────────────────────────

    [Fact]
    public void PurgeOldRuns_RemovesOldCompletedRuns()
    {
        // Record and complete a run
        var runId = _store.RecordRunStart("test-task", new DateOnly(2026, 1, 1));
        _store.RecordRunSuccess(runId, "test-task", new DateOnly(2026, 1, 1));

        // Purge with 0 days retention should remove it
        var purged = _store.PurgeOldRuns(keepDays: 0);
        Assert.True(purged > 0);
    }

    [Fact]
    public void PurgeOldRuns_DoesNotRemoveRecentRuns()
    {
        var runId = _store.RecordRunStart("test-task", new DateOnly(2026, 3, 12));
        _store.RecordRunSuccess(runId, "test-task", new DateOnly(2026, 3, 12));

        // Purge with 30 days retention should keep recent runs
        var purged = _store.PurgeOldRuns(keepDays: 30);
        Assert.Equal(0, purged);
    }

    // ── Persistence across instances ───────────────────────────────────

    [Fact]
    public void State_SurvivesNewInstance()
    {
        var date = new DateOnly(2026, 3, 12);
        _store.SetLastCompletedDate("test-task", date);
        _store.Dispose();

        // Create a new store pointing at the same directory
        using var store2 = new MaintenanceStore(_tempDir);
        Assert.Equal(date, store2.GetLastCompletedDate("test-task"));
    }
}
