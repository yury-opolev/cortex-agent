using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SubagentRunnerRegistryTests
{
    private readonly SubagentRunnerRegistry _registry;

    public SubagentRunnerRegistryTests()
    {
        _registry = new SubagentRunnerRegistry(
            maxConcurrent: 2,
            NullLogger<SubagentRunnerRegistry>.Instance);
    }

    // ── Registration + slots ─────────────────────────────────────────────

    [Fact]
    public void TryRegister_WithinLimit_ReturnsTrue()
    {
        Assert.True(_registry.TryRegister("task-1", CreateRunner(), out _));
        Assert.True(_registry.TryRegister("task-2", CreateRunner(), out _));
    }

    [Fact]
    public void TryRegister_AtLimit_ReturnsFalse()
    {
        _registry.TryRegister("task-1", CreateRunner(), out _);
        _registry.TryRegister("task-2", CreateRunner(), out _);

        Assert.False(_registry.TryRegister("task-3", CreateRunner(), out _));
    }

    [Fact]
    public void Remove_FreesSlot()
    {
        _registry.TryRegister("task-1", CreateRunner(), out _);
        _registry.TryRegister("task-2", CreateRunner(), out _);
        Assert.False(_registry.TryRegister("task-3", CreateRunner(), out _));

        _registry.Remove("task-1");

        Assert.True(_registry.TryRegister("task-3", CreateRunner(), out _));
    }

    [Fact]
    public void Remove_AllTasks_AllSlotsFree()
    {
        _registry.TryRegister("task-1", CreateRunner(), out _);
        _registry.TryRegister("task-2", CreateRunner(), out _);

        _registry.Remove("task-1");
        _registry.Remove("task-2");

        Assert.True(_registry.TryRegister("task-3", CreateRunner(), out _));
        Assert.True(_registry.TryRegister("task-4", CreateRunner(), out _));
    }

    [Fact]
    public void Remove_SameTaskTwice_SafeNoOp()
    {
        _registry.TryRegister("task-1", CreateRunner(), out _);

        Assert.True(_registry.Remove("task-1"));
        Assert.False(_registry.Remove("task-1")); // already removed

        Assert.Equal(0, _registry.ActiveCount);
    }

    // ── Lookup ───────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_Registered_ReturnsRunner()
    {
        var runner = CreateRunner();
        _registry.TryRegister("task-1", runner, out _);

        Assert.Same(runner, _registry.TryGet("task-1"));
    }

    [Fact]
    public void TryGet_NotRegistered_ReturnsNull()
    {
        Assert.Null(_registry.TryGet("nonexistent"));
    }

    [Fact]
    public void GetActiveTaskIds_ReturnsAllRegistered()
    {
        _registry.TryRegister("task-1", CreateRunner(), out _);
        _registry.TryRegister("task-2", CreateRunner(), out _);

        var ids = _registry.GetActiveTaskIds();

        Assert.Equal(2, ids.Count);
        Assert.Contains("task-1", ids);
        Assert.Contains("task-2", ids);
    }

    // ── HasAvailableSlot ─────────────────────────────────────────────────

    [Fact]
    public void HasAvailableSlot_Empty_True()
    {
        Assert.True(_registry.HasAvailableSlot);
    }

    [Fact]
    public void HasAvailableSlot_Full_False()
    {
        _registry.TryRegister("task-1", CreateRunner(), out _);
        _registry.TryRegister("task-2", CreateRunner(), out _);

        Assert.False(_registry.HasAvailableSlot);
    }

    [Fact]
    public void HasAvailableSlot_AfterRemove_True()
    {
        _registry.TryRegister("task-1", CreateRunner(), out _);
        _registry.TryRegister("task-2", CreateRunner(), out _);

        _registry.Remove("task-1");

        Assert.True(_registry.HasAvailableSlot);
    }

    // ── ActiveCount ──────────────────────────────────────────────────────

    [Fact]
    public void ActiveCount_TracksRegistrations()
    {
        Assert.Equal(0, _registry.ActiveCount);

        _registry.TryRegister("task-1", CreateRunner(), out _);
        Assert.Equal(1, _registry.ActiveCount);

        _registry.TryRegister("task-2", CreateRunner(), out _);
        Assert.Equal(2, _registry.ActiveCount);

        _registry.Remove("task-1");
        Assert.Equal(1, _registry.ActiveCount);
    }

    // ── Constructor validation ───────────────────────────────────────────

    [Fact]
    public void Constructor_ZeroMaxConcurrent_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SubagentRunnerRegistry(0, NullLogger<SubagentRunnerRegistry>.Instance));
    }

    [Fact]
    public void Constructor_Maximum50_Succeeds()
    {
        var registry = new SubagentRunnerRegistry(50, NullLogger<SubagentRunnerRegistry>.Instance);

        Assert.Equal(50, registry.MaxConcurrent);
    }

    [Fact]
    public void Constructor_AboveMaximum_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SubagentRunnerRegistry(51, NullLogger<SubagentRunnerRegistry>.Instance));
    }

    // ── Live cap ─────────────────────────────────────────────────────────

    [Fact]
    public void SetMaxConcurrent_Raise_OpensSlotsAndInvokesCallback()
    {
        // fill the cap of 2
        _registry.TryRegister("task-1", CreateRunner(), out _);
        _registry.TryRegister("task-2", CreateRunner(), out _);
        Assert.False(_registry.HasAvailableSlot);

        var callbackCount = 0;
        _registry.SetSlotsOpenedCallback(() => callbackCount++);

        _registry.SetMaxConcurrent(4);

        Assert.Equal(4, _registry.MaxConcurrent);
        Assert.True(_registry.HasAvailableSlot);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void SetMaxConcurrent_Lower_DoesNotInvokeCallback()
    {
        var callbackCount = 0;
        _registry.SetSlotsOpenedCallback(() => callbackCount++);

        _registry.SetMaxConcurrent(1);

        Assert.Equal(1, _registry.MaxConcurrent);
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public void SetMaxConcurrent_AtMaximum_Accepts50()
    {
        _registry.SetMaxConcurrent(50);

        Assert.Equal(50, _registry.MaxConcurrent);
    }

    [Fact]
    public void SetMaxConcurrent_BelowMinimum_ThrowsWithoutChangingValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _registry.SetMaxConcurrent(0));

        Assert.Equal(2, _registry.MaxConcurrent); // unchanged from fixture's cap of 2
    }

    [Fact]
    public void SetMaxConcurrent_AboveMaximum_ThrowsWithoutChangingValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _registry.SetMaxConcurrent(51));

        Assert.Equal(2, _registry.MaxConcurrent); // unchanged from fixture's cap of 2
    }

    [Fact]
    public void SetMaxConcurrent_EqualToCurrent_DoesNotInvokeCallback()
    {
        var callbackCount = 0;
        _registry.SetSlotsOpenedCallback(() => callbackCount++);

        _registry.SetMaxConcurrent(2); // equals the fixture's cap of 2

        Assert.Equal(2, _registry.MaxConcurrent);
        Assert.Equal(0, callbackCount);
    }

    // ── Per-task cancellation ────────────────────────────────────────────

    [Fact]
    public void GetCancellationToken_Registered_ReturnsLiveToken()
    {
        _registry.TryRegister("task-1", CreateRunner(), out _);
        var token = _registry.GetCancellationToken("task-1");
        Assert.False(token.IsCancellationRequested);
        Assert.True(token.CanBeCanceled);
    }

    [Fact]
    public void TryCancel_Running_CancelsToken()
    {
        _registry.TryRegister("task-1", CreateRunner(), out _);
        var token = _registry.GetCancellationToken("task-1");

        Assert.True(_registry.TryCancel("task-1"));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void TryCancel_Unknown_ReturnsFalse()
    {
        Assert.False(_registry.TryCancel("nope"));
    }

    [Fact]
    public void GetCancellationToken_Unknown_ReturnsNone()
    {
        Assert.Equal(CancellationToken.None, _registry.GetCancellationToken("nope"));
    }

    // ── Atomic admission (out-token contract) ────────────────────────────

    [Fact]
    public void TryRegister_ReturnsRegistryOwnedToken()
    {
        var registered = _registry.TryRegister("task-1", CreateRunner(), out var token);

        Assert.True(registered);
        Assert.True(token.CanBeCanceled);
        Assert.False(token.IsCancellationRequested);
        // The handed-back token is the SAME registry-owned token exposed by GetCancellationToken.
        Assert.Equal(_registry.GetCancellationToken("task-1"), token);
    }

    [Fact]
    public void TryRegister_DuplicateTaskId_DoesNotReplaceRunner()
    {
        var first = CreateRunner();
        var second = CreateRunner();

        Assert.True(_registry.TryRegister("task-1", first, out _));
        Assert.False(_registry.TryRegister("task-1", second, out var secondToken));

        // The original runner is untouched; the rejected admission yields no token.
        Assert.Same(first, _registry.TryGet("task-1"));
        Assert.Equal(CancellationToken.None, secondToken);
        Assert.Equal(1, _registry.ActiveCount);
    }

    [Fact]
    public void TryRegister_ConcurrentCalls_NeverExceedsMaximum()
    {
        const int cap = 4;
        const int contenders = 64;
        var registry = new SubagentRunnerRegistry(cap, NullLogger<SubagentRunnerRegistry>.Instance);

        var admitted = 0;
        var start = new ManualResetEventSlim(false);
        var threads = new List<Thread>();
        for (var i = 0; i < contenders; i++)
        {
            var id = $"task-{i}";
            var t = new Thread(() =>
            {
                start.Wait();
                if (registry.TryRegister(id, CreateRunner(), out _))
                {
                    Interlocked.Increment(ref admitted);
                }
            });
            threads.Add(t);
            t.Start();
        }

        start.Set();
        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.Equal(cap, admitted);
        Assert.Equal(cap, registry.ActiveCount);
    }

    [Fact]
    public void TryRegister_ConcurrentAdmissions_AdmitsExactlyMaximum()
    {
        const int cap = 50; // the new configurable maximum
        const int contenders = 200;
        var registry = new SubagentRunnerRegistry(cap, NullLogger<SubagentRunnerRegistry>.Instance);

        var admitted = 0;
        var start = new ManualResetEventSlim(false);
        var threads = new List<Thread>();
        for (var i = 0; i < contenders; i++)
        {
            var id = $"task-{i}";
            var t = new Thread(() =>
            {
                start.Wait();
                if (registry.TryRegister(id, CreateRunner(), out _))
                {
                    Interlocked.Increment(ref admitted);
                }
            });
            threads.Add(t);
            t.Start();
        }

        start.Set();
        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.Equal(cap, admitted);
        Assert.Equal(cap, registry.ActiveCount);
    }

    [Fact]
    public void Remove_Success_InvokesSlotsOpenedCallback()
    {
        var callbackCount = 0;
        _registry.SetSlotsOpenedCallback(() => callbackCount++);
        _registry.TryRegister("task-1", CreateRunner(), out _);

        var removed = _registry.Remove("task-1");

        Assert.True(removed);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void TryCancel_CancelsReturnedToken()
    {
        _registry.TryRegister("task-1", CreateRunner(), out var token);

        Assert.True(_registry.TryCancel("task-1"));
        Assert.True(token.IsCancellationRequested);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static SubagentRunner CreateRunner()
    {
        var mockClient = Substitute.For<ILlmClient>();
        var registry = new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);
        return new SubagentRunner(mockClient, registry, 10, NullLogger<SubagentRunner>.Instance);
    }
}
