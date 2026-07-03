using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodaServeArgsBuilderTests
{
    [Fact]
    public void Build_fresh_prompt_session_has_cwd_sessionId_and_default_mode()
    {
        var args = CodaServeArgsBuilder.Build("sess-1", "C:\\repos\\cortex", CodingPolicy.Prompt, isResume: false);

        Assert.Contains("serve", args);
        Assert.Equal("C:\\repos\\cortex", ArgAfter(args, "--cwd"));
        Assert.Equal("sess-1", ArgAfter(args, "--session-id"));
        Assert.Equal("default", ArgAfter(args, "--permission-mode"));
    }

    [Fact]
    public void Build_yolo_safe_maps_to_yolo_safe_mode()
    {
        var args = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.YoloSafe, isResume: false);
        Assert.Equal("yolo-safe", ArgAfter(args, "--permission-mode"));
    }

    [Fact]
    public void Build_yolo_maps_to_yolo_mode()
    {
        var args = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Yolo, isResume: false);
        Assert.Equal("yolo", ArgAfter(args, "--permission-mode"));
    }

    [Fact]
    public void Build_resume_passes_same_session_id()
    {
        var args = CodaServeArgsBuilder.Build("keep-me", "C:\\x", CodingPolicy.Prompt, isResume: true);
        Assert.Equal("keep-me", ArgAfter(args, "--session-id"));
    }

    [Fact]
    public void Build_with_goal_and_session_memory_adds_flags()
    {
        var args = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.YoloSafe, isResume: false,
            goal: "all green", sessionMemory: true);
        Assert.Equal("all green", ArgAfter(args, "--goal"));
        Assert.Contains("--session-memory", args);
    }

    [Fact]
    public void Build_always_forces_telemetry_and_never_pins_model()
    {
        var args = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Prompt, isResume: false, provider: "github-copilot");

        Assert.Contains("--telemetry", args);
        Assert.DoesNotContain("--model", args);
        Assert.Equal("github-copilot", ArgAfter(args, "--provider"));
    }

    [Fact]
    public void Build_without_provider_omits_provider_flag_but_keeps_telemetry()
    {
        var args = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Prompt, isResume: false);

        Assert.DoesNotContain("--provider", args);
        Assert.Contains("--telemetry", args);
    }

    [Fact]
    public void Build_mcp_off_adds_no_mcp_flag()
    {
        var args = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Off);
        Assert.Contains("--no-mcp", args);
    }

    [Fact]
    public void Build_mcp_host_and_curated_do_not_add_no_mcp_flag()
    {
        // Host uses the machine ~/.coda/.mcp.json; Curated redirects it via CODA_USER_MCP_DIR
        // (an env var, not a serve flag) — neither disables MCP, so no --no-mcp.
        var host = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Host);
        var curated = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Curated);

        Assert.DoesNotContain("--no-mcp", host);
        Assert.DoesNotContain("--no-mcp", curated);
    }

    [Fact]
    public void Build_mcp_curated_suppresses_project_layer()
    {
        // Curated = user (vetted) set only; the repo's <cwd>/.mcp.json must not override it.
        var curated = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Curated);
        Assert.Contains("--no-project-mcp", curated);
    }

    [Fact]
    public void Build_mcp_host_keeps_full_host_visibility()
    {
        // Default policy: coda sees everything (user + project). No suppression flags.
        var host = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Prompt, isResume: false, mcp: CodaMcpPolicy.Host);
        Assert.DoesNotContain("--no-project-mcp", host);
        Assert.DoesNotContain("--no-mcp", host);
    }

    [Fact]
    public void Build_default_mcp_is_host_and_omits_no_mcp_flag()
    {
        var args = CodaServeArgsBuilder.Build("s", "C:\\x", CodingPolicy.Prompt, isResume: false);
        Assert.DoesNotContain("--no-mcp", args);
    }

    private static string ArgAfter(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : "";
    }
}
