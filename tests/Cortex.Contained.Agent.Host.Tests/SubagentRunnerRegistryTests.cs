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
        Assert.True(_registry.TryRegister("task-1", CreateRunner()));
        Assert.True(_registry.TryRegister("task-2", CreateRunner()));
    }

    [Fact]
    public void TryRegister_AtLimit_ReturnsFalse()
    {
        _registry.TryRegister("task-1", CreateRunner());
        _registry.TryRegister("task-2", CreateRunner());

        Assert.False(_registry.TryRegister("task-3", CreateRunner()));
    }

    [Fact]
    public void Remove_FreesSlot()
    {
        _registry.TryRegister("task-1", CreateRunner());
        _registry.TryRegister("task-2", CreateRunner());
        Assert.False(_registry.TryRegister("task-3", CreateRunner()));

        _registry.Remove("task-1");

        Assert.True(_registry.TryRegister("task-3", CreateRunner()));
    }

    [Fact]
    public void Remove_AllTasks_AllSlotsFree()
    {
        _registry.TryRegister("task-1", CreateRunner());
        _registry.TryRegister("task-2", CreateRunner());

        _registry.Remove("task-1");
        _registry.Remove("task-2");

        Assert.True(_registry.TryRegister("task-3", CreateRunner()));
        Assert.True(_registry.TryRegister("task-4", CreateRunner()));
    }

    [Fact]
    public void Remove_SameTaskTwice_SafeNoOp()
    {
        _registry.TryRegister("task-1", CreateRunner());

        Assert.True(_registry.Remove("task-1"));
        Assert.False(_registry.Remove("task-1")); // already removed

        Assert.Equal(0, _registry.ActiveCount);
    }

    // ── Lookup ───────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_Registered_ReturnsRunner()
    {
        var runner = CreateRunner();
        _registry.TryRegister("task-1", runner);

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
        _registry.TryRegister("task-1", CreateRunner());
        _registry.TryRegister("task-2", CreateRunner());

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
        _registry.TryRegister("task-1", CreateRunner());
        _registry.TryRegister("task-2", CreateRunner());

        Assert.False(_registry.HasAvailableSlot);
    }

    [Fact]
    public void HasAvailableSlot_AfterRemove_True()
    {
        _registry.TryRegister("task-1", CreateRunner());
        _registry.TryRegister("task-2", CreateRunner());

        _registry.Remove("task-1");

        Assert.True(_registry.HasAvailableSlot);
    }

    // ── ActiveCount ──────────────────────────────────────────────────────

    [Fact]
    public void ActiveCount_TracksRegistrations()
    {
        Assert.Equal(0, _registry.ActiveCount);

        _registry.TryRegister("task-1", CreateRunner());
        Assert.Equal(1, _registry.ActiveCount);

        _registry.TryRegister("task-2", CreateRunner());
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

    // ── Live cap ─────────────────────────────────────────────────────────

    [Fact]
    public void SetMaxConcurrent_Raise_OpensSlotsAndInvokesCallback()
    {
        // fill the cap of 2
        _registry.TryRegister("task-1", CreateRunner());
        _registry.TryRegister("task-2", CreateRunner());
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

    [Theory]
    [InlineData(0, 1)]
    [InlineData(25, 20)]
    [InlineData(7, 7)]
    public void SetMaxConcurrent_Clamps_To_1_20(int input, int expected)
    {
        _registry.SetMaxConcurrent(input);
        Assert.Equal(expected, _registry.MaxConcurrent);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static SubagentRunner CreateRunner()
    {
        var mockClient = Substitute.For<ILlmClient>();
        var registry = new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);
        return new SubagentRunner(mockClient, registry, 10, NullLogger<SubagentRunner>.Instance);
    }
}
