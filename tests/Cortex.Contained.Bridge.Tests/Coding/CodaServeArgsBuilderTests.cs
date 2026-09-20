using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodaServeArgsBuilderTests
{
    [Fact]
    public void Build_fresh_prompt_session_has_cwd_and_default_mode()
    {
        var args = CodaServeArgsBuilder.Build("C:\\repos\\cortex", CodingPolicy.Prompt, isResume: false);

        Assert.Contains("serve", args);
        Assert.Equal("C:\\repos\\cortex", ArgAfter(args, "--cwd"));
        Assert.Equal("default", ArgAfter(args, "--permission-mode"));
    }

    [Fact]
    public void Build_never_passes_session_id_because_coda_retired_the_flag()
    {
        // coda resumes via the `sessionId` param on `initialize`, not a CLI flag. Passing
        // --session-id to a current coda is a hard clap failure ("unexpected argument"),
        // which would take down every Cortex-spawned session.
        var fresh = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false);
        var resumed = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: true);

        Assert.DoesNotContain("--session-id", fresh);
        Assert.DoesNotContain("--session-id", resumed);
    }

    [Fact]
    public void Build_yolo_safe_maps_to_accept_edits()
    {
        // coda retired `yolo-safe` and rejects it at startup rather than downgrading it.
        // acceptEdits is the nearest surviving meaning: edits apply silently, shell
        // commands still prompt.
        var args = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.YoloSafe, isResume: false);
        Assert.Equal("acceptEdits", ArgAfter(args, "--permission-mode"));
    }

    [Fact]
    public void Build_yolo_maps_to_bypass_permissions()
    {
        var args = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Yolo, isResume: false);
        Assert.Equal("bypassPermissions", ArgAfter(args, "--permission-mode"));
    }

    [Theory]
    [InlineData(CodingPolicy.Prompt)]
    [InlineData(CodingPolicy.YoloSafe)]
    [InlineData(CodingPolicy.Yolo)]
    public void Build_only_emits_permission_modes_coda_accepts(CodingPolicy policy)
    {
        // coda's accepted set. An unrecognised mode is a startup failure, so a new policy
        // must never reach the CLI without a deliberate mapping.
        string[] accepted = ["default", "acceptEdits", "plan", "bypassPermissions"];

        var args = CodaServeArgsBuilder.Build("C:\\x", policy, isResume: false);

        Assert.Contains(ArgAfter(args, "--permission-mode"), accepted);
    }

    [Fact]
    public void Build_with_goal_adds_the_goal_flag()
    {
        var args = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.YoloSafe, isResume: false, goal: "all green");
        Assert.Equal("all green", ArgAfter(args, "--goal"));
    }

    [Fact]
    public void Build_without_goal_omits_the_goal_flag()
    {
        var args = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false);
        Assert.DoesNotContain("--goal", args);
    }

    [Fact]
    public void Build_forces_diagnostics_and_never_pins_model_or_provider()
    {
        // Diagnostics replaces the retired --telemetry flag: it is what produces the
        // per-run log coda reports back as `telemetryLogPath` from `initialize`.
        var args = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false);

        Assert.Equal("debug", ArgAfter(args, "--diagnostic-verbosity"));
        Assert.DoesNotContain("--telemetry", args);
        Assert.DoesNotContain("--model", args);
        Assert.DoesNotContain("--provider", args);
    }

    [Fact]
    public void Build_never_passes_session_memory_because_coda_retired_it()
    {
        var args = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false, goal: "g");
        Assert.DoesNotContain("--session-memory", args);
    }

    [Fact]
    public void Build_mcp_off_adds_no_mcp_flag()
    {
        var args = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Off);
        Assert.Contains("--no-mcp", args);
    }

    [Fact]
    public void Build_mcp_host_and_curated_do_not_add_no_mcp_flag()
    {
        // Host uses the machine ~/.coda/.mcp.json; Curated redirects it via CODA_USER_MCP_DIR
        // (an env var, not a serve flag) — neither disables MCP, so no --no-mcp.
        var host = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Host);
        var curated = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Curated);

        Assert.DoesNotContain("--no-mcp", host);
        Assert.DoesNotContain("--no-mcp", curated);
    }

    [Fact]
    public void Build_mcp_curated_suppresses_project_layer()
    {
        // Curated = user (vetted) set only; the repo's <cwd>/.mcp.json must not override it.
        var curated = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Curated);
        Assert.Contains("--no-project-mcp", curated);
    }

    [Fact]
    public void Build_mcp_host_keeps_full_host_visibility()
    {
        // Default policy: coda sees everything (user + project). No suppression flags.
        var host = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Host);
        Assert.DoesNotContain("--no-project-mcp", host);
        Assert.DoesNotContain("--no-mcp", host);
    }

    [Fact]
    public void Build_default_mcp_is_host_and_omits_no_mcp_flag()
    {
        var args = CodaServeArgsBuilder.Build("C:\\x", CodingPolicy.Prompt, isResume: false);
        Assert.DoesNotContain("--no-mcp", args);
    }

    private static string ArgAfter(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : "";
    }
}
