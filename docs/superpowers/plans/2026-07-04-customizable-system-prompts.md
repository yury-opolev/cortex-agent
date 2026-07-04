# Customizable System Prompts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the main-agent and subagent system prompts fully customizable (template layout + authorable prose blocks) via the Bridge web UI, without letting a bad edit silently break tool behavior, and with defaults that render byte-identical to today.

**Architecture:** A placeholder-template model. Two editable templates (main, subagent) contain `{{placeholders}}`; runtime-computed segments stay as validated placeholders while authorable prose (persona, voice-mode, coding-relay, subagent-instructions) is editable. A pure `SystemPromptRenderer` substitutes values; a pure `SystemPromptValidator` guards edits; a file-backed `SystemPromptStore` persists config in the container data volume with defaults from a shared `SystemPromptDefaults`. `PromptAssembler` and `SubAgentStartTool` are refactored to resolve placeholder values and render, replacing hardcoded concatenation.

**Tech Stack:** .NET 10, C# latest, xUnit + NSubstitute, SignalR hub contracts, ASP.NET Core minimal APIs, Alpine.js web UI.

## Global Constraints

- C# style per user global CLAUDE.md: `this.` on instance members; braces always; one type per file; file-scoped namespaces; `var` when obvious; raw string literals for multi-line; `sealed`; `readonly`; source-generated `[LoggerMessage]` for logging; `ConfigureAwait(false)` in agent/library code; async methods suffixed `Async`.
- `TreatWarningsAsErrors` is ON globally; `AnalysisLevel=latest-recommended`. Code must compile clean.
- DTOs serialized to JSON must use proper named properties (NOT ValueTuples — `Item1`/`Item2` do not serialize as camelCase). Add a serialization-shape regression test.
- Behavior-preservation contract: default templates + segment texts MUST reassemble the current hardcoded strings in the current order → byte-identical prompt output. Characterization tests (Task 5) are the acceptance gate and must stay green through Tasks 6–7.
- Char caps: main/subagent templates ≤ 8000 chars; segment texts (`VoiceMode`, `CodingRelay`, `SubagentInstructions`) ≤ 4000 chars.
- Conditional placeholders carry their own leading whitespace (exactly as today's code does), so an absent conditional contributes `""` with no spacing gap. The renderer does NOT collapse blank lines — byte-identity is achieved by moving the existing strings verbatim and preserving assembly order.
- All new hub methods route through the existing composed `IAgentHub` surface; add them to `IChatHub` (where personality lives) and mirror the personality call pattern in `HubClient`.
- Reuse existing Bridge admin auth on all new REST endpoints (mirror the personality endpoints exactly).

**Build:** `dotnet build cortex-contained.sln`
**Test (agent):** `dotnet test tests/Cortex.Contained.Agent.Host.Tests`
**Test (contracts):** `dotnet test tests/Cortex.Contained.Contracts.Tests`

---

### Task 1: Contracts — config, validation result, placeholder catalog, defaults

**Files:**
- Create: `src/Cortex.Contained.Contracts/SystemPrompt/SystemPromptConfig.cs`
- Create: `src/Cortex.Contained.Contracts/SystemPrompt/SystemPromptValidationResult.cs`
- Create: `src/Cortex.Contained.Contracts/SystemPrompt/SystemPromptPlaceholders.cs`
- Create: `src/Cortex.Contained.Contracts/SystemPrompt/SystemPromptDefaults.cs`
- Test: `tests/Cortex.Contained.Contracts.Tests/SystemPromptConfigTests.cs`

**Interfaces:**
- Produces:
  - `SystemPromptConfig { string MainTemplate; string SubagentTemplate; string VoiceMode; string CodingRelay; string SubagentInstructions }` (all `get; set;`, defaulting to the matching `SystemPromptDefaults` value).
  - `SystemPromptValidationResult { bool IsValid; List<string> Errors; List<string> Warnings }`.
  - `SystemPromptPlaceholders`: `static readonly FrozenSet<string> Main`, `Subagent`; `const int TemplateMaxChars = 8000`, `SegmentMaxChars = 4000`; `static readonly string[] MainRecommended`, `SubagentRecommended`.
  - `SystemPromptDefaults`: `const string MainTemplate`, `SubagentTemplate`, `VoiceMode`, `CodingRelay`, `SubagentInstructions`; `static SystemPromptConfig Create()` returning a fresh config populated with the defaults.

- [ ] **Step 1: Write the failing test**

Create `tests/Cortex.Contained.Contracts.Tests/SystemPromptConfigTests.cs`:

```csharp
using System.Text.Json;
using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Contracts.Tests;

public class SystemPromptConfigTests
{
    [Fact]
    public void Defaults_Create_PopulatesAllFieldsFromDefaults()
    {
        var config = SystemPromptDefaults.Create();

        Assert.Equal(SystemPromptDefaults.MainTemplate, config.MainTemplate);
        Assert.Equal(SystemPromptDefaults.SubagentTemplate, config.SubagentTemplate);
        Assert.Equal(SystemPromptDefaults.VoiceMode, config.VoiceMode);
        Assert.Equal(SystemPromptDefaults.CodingRelay, config.CodingRelay);
        Assert.Equal(SystemPromptDefaults.SubagentInstructions, config.SubagentInstructions);
    }

    [Fact]
    public void MainTemplate_ContainsAllMainPlaceholders()
    {
        foreach (var name in SystemPromptPlaceholders.Main)
        {
            Assert.Contains("{{" + name + "}}", SystemPromptDefaults.MainTemplate, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SubagentTemplate_ContainsInstructionsPlaceholder()
    {
        Assert.Contains("{{instructions}}", SystemPromptDefaults.SubagentTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialization_UsesCamelCaseNamedProperties_NoItem1()
    {
        var config = SystemPromptDefaults.Create();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var json = JsonSerializer.Serialize(config, options);

        Assert.Contains("\"mainTemplate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"subagentTemplate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"voiceMode\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Item1", json, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize<SystemPromptConfig>(json, options)!;
        Assert.Equal(config.CodingRelay, roundTrip.CodingRelay);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Contracts.Tests --filter "ClassName=SystemPromptConfigTests"`
Expected: FAIL — types do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

`SystemPromptConfig.cs`:

```csharp
namespace Cortex.Contained.Contracts.SystemPrompt;

/// <summary>
/// User-editable system-prompt configuration: the two prompt templates plus the
/// authorable prose segments. Persisted per container (per tenant) by the agent.
/// </summary>
public sealed class SystemPromptConfig
{
    /// <summary>Main-agent (and scheduled/task-run) prompt template with {{placeholders}}.</summary>
    public string MainTemplate { get; set; } = SystemPromptDefaults.MainTemplate;

    /// <summary>Subagent prompt template with {{placeholders}}.</summary>
    public string SubagentTemplate { get; set; } = SystemPromptDefaults.SubagentTemplate;

    /// <summary>Authorable voice-mode block (injected only on voice channels).</summary>
    public string VoiceMode { get; set; } = SystemPromptDefaults.VoiceMode;

    /// <summary>Authorable coding-agent relay block.</summary>
    public string CodingRelay { get; set; } = SystemPromptDefaults.CodingRelay;

    /// <summary>Authorable fixed instructions block for subagents.</summary>
    public string SubagentInstructions { get; set; } = SystemPromptDefaults.SubagentInstructions;
}
```

`SystemPromptValidationResult.cs`:

```csharp
namespace Cortex.Contained.Contracts.SystemPrompt;

/// <summary>Result of validating a <see cref="SystemPromptConfig"/> before persisting.</summary>
public sealed class SystemPromptValidationResult
{
    /// <summary>True when there are no blocking errors.</summary>
    public bool IsValid { get; set; }

    /// <summary>Blocking problems (unknown placeholder, cap exceeded). Non-empty ⇒ not saved.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>Non-blocking advisories (missing recommended placeholder).</summary>
    public List<string> Warnings { get; set; } = [];
}
```

`SystemPromptPlaceholders.cs`:

```csharp
using System.Collections.Frozen;

namespace Cortex.Contained.Contracts.SystemPrompt;

/// <summary>Catalog of valid placeholder names and size limits for prompt templates.</summary>
public static class SystemPromptPlaceholders
{
    /// <summary>Maximum characters for a template body.</summary>
    public const int TemplateMaxChars = 8000;

    /// <summary>Maximum characters for an authorable prose segment.</summary>
    public const int SegmentMaxChars = 4000;

    /// <summary>Placeholder names allowed in the main template.</summary>
    public static readonly FrozenSet<string> Main = FrozenSet.ToFrozenSet(
        [
            "personality", "self_notes", "skills", "channel",
            "voice_mode", "active_tasks", "active_plans", "coding_relay",
        ], StringComparer.Ordinal);

    /// <summary>Placeholder names allowed in the subagent template.</summary>
    public static readonly FrozenSet<string> Subagent = FrozenSet.ToFrozenSet(
        [
            "personality", "skill", "instructions", "skills",
            "bootstrap_context", "recalled_memories",
        ], StringComparer.Ordinal);

    /// <summary>Main placeholders whose absence is worth warning about.</summary>
    public static readonly string[] MainRecommended =
        ["personality", "self_notes", "skills", "coding_relay"];

    /// <summary>Subagent placeholders whose absence is worth warning about.</summary>
    public static readonly string[] SubagentRecommended =
        ["instructions", "skills"];
}
```

`SystemPromptDefaults.cs` — author the templates and paste the segment texts. The `VoiceMode`, `CodingRelay`, and `SubagentInstructions` constant BODIES must be copied VERBATIM from the existing source so output stays byte-identical:
- `VoiceMode` ⇐ the body of `VoiceModeInstructions` in `src/Cortex.Contained.Agent.Host/Agent/PromptAssembler.cs:41-70` (the full raw string, unchanged).
- `CodingRelay` ⇐ the body of `CodingAgentRelayPrompt` in `PromptAssembler.cs:78-186` (the full raw string, unchanged).
- `SubagentInstructions` ⇐ the six lines appended in `SubAgentStartTool.BuildSubagentSystemPrompt` (`SubAgentStartTool.cs:576-581`) joined as one raw string ending with a trailing newline.

```csharp
namespace Cortex.Contained.Contracts.SystemPrompt;

/// <summary>
/// Shared default templates and authorable segment texts for the system prompt.
/// Defaults reassemble the historical hardcoded prompt byte-for-byte. Shared by the
/// Agent (rendering + reset) and the Bridge (reset).
/// </summary>
public static class SystemPromptDefaults
{
    /// <summary>
    /// Default main template. Conditional placeholders ({{channel}}, {{voice_mode}},
    /// {{active_tasks}}, {{active_plans}}) resolve to "" when absent and carry their own
    /// leading whitespace when present, matching the historical assembly exactly.
    /// </summary>
    public const string MainTemplate =
        "{{personality}}\n\n## Self-notes\n{{self_notes}}{{skills}}{{channel}}{{voice_mode}}{{active_tasks}}{{active_plans}}{{coding_relay}}";

    /// <summary>
    /// Default subagent template. {{personality}} is empty by default (persona is not
    /// injected into subagents unless the user opts in).
    /// </summary>
    public const string SubagentTemplate =
        "{{personality}}{{skill}}{{instructions}}{{skills}}{{bootstrap_context}}{{recalled_memories}}";

    /// <summary>Voice-mode block — verbatim copy of the former VoiceModeInstructions.</summary>
    public const string VoiceMode = """
        <PASTE VoiceModeInstructions body verbatim from PromptAssembler.cs:41-70>
        """;

    /// <summary>Coding-agent relay block — verbatim copy of the former CodingAgentRelayPrompt.</summary>
    public const string CodingRelay = """
        <PASTE CodingAgentRelayPrompt body verbatim from PromptAssembler.cs:78-186>
        """;

    /// <summary>Fixed subagent instructions — verbatim copy of the SubAgentStartTool block.</summary>
    public const string SubagentInstructions = """
        <PASTE the 6 instruction lines verbatim from SubAgentStartTool.cs:576-581, trailing newline>
        """;

    /// <summary>Create a config populated with all defaults.</summary>
    public static SystemPromptConfig Create() => new();
}
```

> NOTE for the implementer: because `SystemPromptConfig`'s property initializers already
> reference these constants, `Create()` returning `new()` yields all defaults. When you paste
> the verbatim bodies, do NOT reformat, re-indent, or "clean up" whitespace — byte-identity in
> Task 5–7 depends on an exact copy. The subagent template intentionally has NO `## Skill:`
> header text in the template itself; that header is part of the `{{skill}}` value (Task 7).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Contracts.Tests --filter "ClassName=SystemPromptConfigTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Contracts/SystemPrompt tests/Cortex.Contained.Contracts.Tests/SystemPromptConfigTests.cs
git commit -m "feat(contracts): system-prompt config, validation result, placeholders, defaults"
```

---

### Task 2: SystemPromptRenderer (pure)

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Agent/SystemPromptRenderer.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/SystemPromptRendererTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static class SystemPromptRenderer` with
  `static string Render(string template, IReadOnlyDictionary<string, string> values)`.
  Replaces every `{{name}}` whose `name` exists in `values` with that value; placeholder names
  are `[a-z_]+`. Placeholders absent from `values` are left untouched (validator guarantees
  templates only use known names; the resolver always supplies every known name).

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

public class SystemPromptRendererTests
{
    [Fact]
    public void Render_SubstitutesKnownPlaceholders()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["personality"] = "You are Cortex.",
            ["self_notes"] = "notes",
        };

        var result = SystemPromptRenderer.Render("{{personality}}\n\n## Self-notes\n{{self_notes}}", values);

        Assert.Equal("You are Cortex.\n\n## Self-notes\nnotes", result);
    }

    [Fact]
    public void Render_EmptyValue_ProducesNoGap()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "X",
            ["channel"] = "",
            ["b"] = "Y",
        };

        var result = SystemPromptRenderer.Render("{{a}}{{channel}}{{b}}", values);

        Assert.Equal("XY", result);
    }

    [Fact]
    public void Render_RepeatedPlaceholder_ReplacesAllOccurrences()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["x"] = "1" };

        Assert.Equal("1-1", SystemPromptRenderer.Render("{{x}}-{{x}}", values));
    }

    [Fact]
    public void Render_UnknownPlaceholder_LeftUntouched()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["x"] = "1" };

        Assert.Equal("1-{{y}}", SystemPromptRenderer.Render("{{x}}-{{y}}", values));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptRendererTests"`
Expected: FAIL — `SystemPromptRenderer` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.RegularExpressions;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Pure placeholder renderer for system-prompt templates. Substitutes every
/// <c>{{name}}</c> whose name is present in the supplied values. Names not present
/// are left untouched (the validator guarantees templates use only known names and the
/// resolver always supplies every known name — empty string when a segment is absent).
/// </summary>
public static partial class SystemPromptRenderer
{
    [GeneratedRegex(@"\{\{([a-z_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>Render <paramref name="template"/> against <paramref name="values"/>.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        return PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            return values.TryGetValue(name, out var value) ? value : match.Value;
        });
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptRendererTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Agent/SystemPromptRenderer.cs tests/Cortex.Contained.Agent.Host.Tests/SystemPromptRendererTests.cs
git commit -m "feat(agent): pure SystemPromptRenderer for placeholder templates"
```

---

### Task 3: SystemPromptValidator (pure)

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Agent/SystemPromptValidator.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/SystemPromptValidatorTests.cs`

**Interfaces:**
- Consumes: `SystemPromptConfig`, `SystemPromptValidationResult`, `SystemPromptPlaceholders` (Contracts).
- Produces: `static class SystemPromptValidator` with
  `static SystemPromptValidationResult Validate(SystemPromptConfig config)`.
  Errors: unknown placeholder in either template; any field over its cap. Warnings: missing
  recommended placeholder. `IsValid = Errors.Count == 0`.

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Agent.Host.Tests;

public class SystemPromptValidatorTests
{
    [Fact]
    public void Validate_Defaults_IsValidNoErrors()
    {
        var result = SystemPromptValidator.Validate(SystemPromptDefaults.Create());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_UnknownPlaceholder_IsError()
    {
        var config = SystemPromptDefaults.Create();
        config.MainTemplate = "{{personality}} {{bogus_thing}}";

        var result = SystemPromptValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("bogus_thing", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_OverCap_IsError()
    {
        var config = SystemPromptDefaults.Create();
        config.CodingRelay = new string('x', SystemPromptPlaceholders.SegmentMaxChars + 1);

        var result = SystemPromptValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("CodingRelay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_MissingRecommended_IsWarningNotError()
    {
        var config = SystemPromptDefaults.Create();
        config.MainTemplate = "{{personality}} {{self_notes}} {{skills}}"; // no coding_relay

        var result = SystemPromptValidator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("coding_relay", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptValidatorTests"`
Expected: FAIL — `SystemPromptValidator` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.RegularExpressions;
using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Pure validator for <see cref="SystemPromptConfig"/>. Blocks unknown placeholders and
/// oversized fields; warns on missing recommended placeholders.
/// </summary>
public static partial class SystemPromptValidator
{
    [GeneratedRegex(@"\{\{([a-z_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>Validate the configuration.</summary>
    public static SystemPromptValidationResult Validate(SystemPromptConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var result = new SystemPromptValidationResult();

        ValidateTemplate(result, "MainTemplate", config.MainTemplate,
            SystemPromptPlaceholders.Main, SystemPromptPlaceholders.MainRecommended);
        ValidateTemplate(result, "SubagentTemplate", config.SubagentTemplate,
            SystemPromptPlaceholders.Subagent, SystemPromptPlaceholders.SubagentRecommended);

        CheckSegmentCap(result, "VoiceMode", config.VoiceMode);
        CheckSegmentCap(result, "CodingRelay", config.CodingRelay);
        CheckSegmentCap(result, "SubagentInstructions", config.SubagentInstructions);

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static void ValidateTemplate(
        SystemPromptValidationResult result, string field, string template,
        System.Collections.Frozen.FrozenSet<string> allowed, string[] recommended)
    {
        if (template.Length > SystemPromptPlaceholders.TemplateMaxChars)
        {
            result.Errors.Add($"{field} exceeds {SystemPromptPlaceholders.TemplateMaxChars} characters.");
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in PlaceholderRegex().Matches(template))
        {
            var name = m.Groups[1].Value;
            used.Add(name);
            if (!allowed.Contains(name))
            {
                result.Errors.Add($"{field} uses unknown placeholder {{{{{name}}}}}.");
            }
        }

        foreach (var name in recommended)
        {
            if (!used.Contains(name))
            {
                result.Warnings.Add($"{field} is missing recommended placeholder {{{{{name}}}}}.");
            }
        }
    }

    private static void CheckSegmentCap(SystemPromptValidationResult result, string field, string value)
    {
        if (value.Length > SystemPromptPlaceholders.SegmentMaxChars)
        {
            result.Errors.Add($"{field} exceeds {SystemPromptPlaceholders.SegmentMaxChars} characters.");
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptValidatorTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Agent/SystemPromptValidator.cs tests/Cortex.Contained.Agent.Host.Tests/SystemPromptValidatorTests.cs
git commit -m "feat(agent): pure SystemPromptValidator (unknown placeholders, caps, warnings)"
```

---

### Task 4: SystemPromptStore (file-backed, cached, atomic, fingerprint)

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Agent/SystemPromptStore.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/SystemPromptStoreTests.cs`

**Interfaces:**
- Consumes: `SystemPromptConfig`, `SystemPromptValidationResult`, `SystemPromptDefaults`, `SystemPromptValidator`.
- Produces: `sealed class SystemPromptStore`:
  - ctor `(string filePath, ILogger<SystemPromptStore> logger)`
  - `SystemPromptConfig Read()` — cached; returns defaults if file missing/empty/corrupt.
  - `SystemPromptValidationResult Write(SystemPromptConfig config)` — validates; on `IsValid` writes atomically + invalidates cache; on invalid returns result WITHOUT writing.
  - `SystemPromptConfig Reset()` — writes defaults, returns them.
  - `string Fingerprint()` — 8-hex-char SHA256 of the active config (telemetry).
  - `string FilePath { get; }`

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.SystemPrompt;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SystemPromptStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "sp-store-" + Guid.NewGuid().ToString("N"));

    private SystemPromptStore NewStore() =>
        new(Path.Combine(this.dir, "system-prompt.json"), NullLogger<SystemPromptStore>.Instance);

    [Fact]
    public void Read_MissingFile_ReturnsDefaults()
    {
        var config = NewStore().Read();
        Assert.Equal(SystemPromptDefaults.MainTemplate, config.MainTemplate);
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var store = NewStore();
        var config = SystemPromptDefaults.Create();
        config.CodingRelay = "custom relay";

        var result = store.Write(config);

        Assert.True(result.IsValid);
        Assert.Equal("custom relay", NewStore().Read().CodingRelay); // fresh store reads from disk
    }

    [Fact]
    public void Write_Invalid_DoesNotPersist()
    {
        var store = NewStore();
        var bad = SystemPromptDefaults.Create();
        bad.MainTemplate = "{{nope}}";

        var result = store.Write(bad);

        Assert.False(result.IsValid);
        Assert.Equal(SystemPromptDefaults.MainTemplate, NewStore().Read().MainTemplate);
    }

    [Fact]
    public void Reset_RestoresDefaults()
    {
        var store = NewStore();
        var config = SystemPromptDefaults.Create();
        config.VoiceMode = "changed";
        store.Write(config);

        var reset = store.Reset();

        Assert.Equal(SystemPromptDefaults.VoiceMode, reset.VoiceMode);
        Assert.Equal(SystemPromptDefaults.VoiceMode, NewStore().Read().VoiceMode);
    }

    [Fact]
    public void Fingerprint_ChangesWhenConfigChanges()
    {
        var store = NewStore();
        var before = store.Fingerprint();

        var config = SystemPromptDefaults.Create();
        config.CodingRelay = "different";
        store.Write(config);

        Assert.NotEqual(before, store.Fingerprint());
    }

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptStoreTests"`
Expected: FAIL — `SystemPromptStore` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// File-backed store for <see cref="SystemPromptConfig"/>. Persists one JSON file in the
/// container data volume (per container = per tenant). Missing/corrupt file falls back to
/// defaults. Reads are cached and invalidated on file write-time change. Writes are atomic
/// (temp file + rename) and validated — an invalid config is rejected without persisting.
/// </summary>
public sealed partial class SystemPromptStore
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string filePath;
    private readonly ILogger<SystemPromptStore> logger;
    private readonly object cacheLock = new();
    private SystemPromptConfig? cached;
    private DateTime cachedWriteUtc;

    public SystemPromptStore(string filePath, ILogger<SystemPromptStore> logger)
    {
        this.filePath = filePath;
        this.logger = logger;
    }

    /// <summary>Path to the config file (diagnostics).</summary>
    public string FilePath => this.filePath;

    /// <summary>Read the active config, falling back to defaults on any problem.</summary>
    public SystemPromptConfig Read()
    {
        try
        {
            if (File.Exists(this.filePath))
            {
                var writeUtc = File.GetLastWriteTimeUtc(this.filePath);
                lock (this.cacheLock)
                {
                    if (this.cached is not null && this.cachedWriteUtc == writeUtc)
                    {
                        return this.cached;
                    }
                }

                var json = File.ReadAllText(this.filePath);
                var parsed = JsonSerializer.Deserialize<SystemPromptConfig>(json, jsonOptions);
                if (parsed is not null)
                {
                    lock (this.cacheLock)
                    {
                        this.cached = parsed;
                        this.cachedWriteUtc = writeUtc;
                    }

                    return parsed;
                }
            }
        }
#pragma warning disable CA1031 // Corrupt/unreadable config must never crash prompt building
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogReadFailed(this.filePath, ex.Message);
        }

        return SystemPromptDefaults.Create();
    }

    /// <summary>Validate and, if valid, persist atomically.</summary>
    public SystemPromptValidationResult Write(SystemPromptConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var validation = SystemPromptValidator.Validate(config);
        if (!validation.IsValid)
        {
            this.LogWriteRejected(string.Join("; ", validation.Errors));
            return validation;
        }

        try
        {
            var dir = Path.GetDirectoryName(this.filePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(config, jsonOptions);
            var tmp = this.filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, this.filePath, overwrite: true);

            lock (this.cacheLock)
            {
                this.cached = config;
                this.cachedWriteUtc = File.GetLastWriteTimeUtc(this.filePath);
            }
        }
#pragma warning disable CA1031 // Surface write failure as an error result, do not crash
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogWriteFailed(this.filePath, ex.Message);
            validation.IsValid = false;
            validation.Errors.Add($"Failed to persist system-prompt config: {ex.Message}");
        }

        return validation;
    }

    /// <summary>Reset to defaults and persist.</summary>
    public SystemPromptConfig Reset()
    {
        var defaults = SystemPromptDefaults.Create();
        this.Write(defaults);
        return defaults;
    }

    /// <summary>Stable 8-hex-char fingerprint of the active config (telemetry correlation).</summary>
    public string Fingerprint()
    {
        var c = this.Read();
        var material = string.Concat(
            c.MainTemplate, c.SubagentTemplate, c.VoiceMode, c.CodingRelay, c.SubagentInstructions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[system-prompt] read failed for {Path}: {Reason}; using defaults")]
    private partial void LogReadFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[system-prompt] write rejected (invalid): {Errors}")]
    private partial void LogWriteRejected(string errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "[system-prompt] write failed for {Path}: {Reason}")]
    private partial void LogWriteFailed(string path, string reason);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptStoreTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Agent/SystemPromptStore.cs tests/Cortex.Contained.Agent.Host.Tests/SystemPromptStoreTests.cs
git commit -m "feat(agent): file-backed SystemPromptStore (cache, atomic write, validation, fingerprint)"
```

---

### Task 5: Characterization tests — lock current prompt output

**Files:**
- Test: `tests/Cortex.Contained.Agent.Host.Tests/SystemPromptCharacterizationTests.cs`

**Interfaces:**
- Consumes: current `PromptAssembler` and `SubAgentStartTool.BuildSubagentSystemPrompt` behavior.
- Produces: golden assertions that Tasks 6–7 must keep green.

This task captures the CURRENT output as literal expected strings so the refactor is provably
behavior-preserving. First confirm the test project has internals access; if
`SystemPromptCharacterizationTests` cannot see `PromptAssembler`, add
`[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Cortex.Contained.Agent.Host.Tests")]`
to `src/Cortex.Contained.Agent.Host/Properties/AssemblyInfo.cs` (create if absent — check for an
existing `InternalsVisibleTo` first; other internal types are already tested, so it likely exists).

- [ ] **Step 1: Write the characterization test (expected values filled after first run)**

Build a `PromptAssembler` with all-null optional stores and a stub `IModelProvider`. Because
`skillRegistry`/`selfNotesStore`/`subagentStore`/`todoResolver` are null, the assembled main
prompt is `personality + "\n\n## Self-notes\n" + "" + CodingRelay`. Capture the exact string.

```csharp
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

public class SystemPromptCharacterizationTests
{
    private static PromptAssembler NewAssembler()
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.ContextWindow.Returns(128_000);
        modelProvider.DefaultModel.Returns("test-model");
        var imageAging = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAging.CurrentValue.Returns(new ImageAgingConfig());

        return new PromptAssembler(
            () => "You are a test.",
            modelProvider,
            imageAging,
            NullLogger<PromptAssembler>.Instance);
    }

    [Fact]
    public async Task MainPrompt_Default_MatchesGolden()
    {
        var session = new AgentSession("conv-1");
        var messages = await NewAssembler().BuildPromptAsync(session, CancellationToken.None);

        var system = messages[0].Content!;
        // With all optional stores null, the current assembler yields exactly:
        //   persona + "\n\n## Self-notes\n" + selfNotes("") + skills("") + channel("")
        //   + voice("") + tasks("") + plans("") + CodingRelay
        // After Task 1, SystemPromptDefaults.CodingRelay holds that exact block, so:
        var expected = "You are a test.\n\n## Self-notes\n"
            + Cortex.Contained.Contracts.SystemPrompt.SystemPromptDefaults.CodingRelay;
        Assert.Equal(expected, system);
    }
}
```

> The implementer: after Task 1 the constant text is available. Assert the full system string
> equals `"You are a test.\n\n## Self-notes\n" + <current coding-relay text>`. Use
> `Assert.Equal` on the whole string for the strongest lock. If constructing the full literal
> is unwieldy, assert `StartsWith` the persona+self-notes prefix AND `Contains` the exact
> coding-relay body AND that no other `##` sections appear. Capture the REAL value by running
> once and pasting. Add an equivalent characterization for `BuildSubagentSystemPrompt` with a
> null skill and null memories/bootstrap (call it via a small internal-visible seam or by
> extracting the method — see Task 7; if not yet callable, defer the subagent golden to Task 7's
> red step).

- [ ] **Step 2: Run to capture actual output**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptCharacterizationTests"`
Expected: FAIL first (placeholder expected); paste the actual system string into `expected`, re-run → PASS.

- [ ] **Step 3: Commit the green characterization**

```bash
git add tests/Cortex.Contained.Agent.Host.Tests/SystemPromptCharacterizationTests.cs
git commit -m "test(agent): characterize current main/subagent system-prompt output (behavior lock)"
```

---

### Task 6: Refactor PromptAssembler to render from store

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Agent/PromptAssembler.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (register `SystemPromptStore`; pass to `PromptAssembler` — but `PromptAssembler` is constructed in `AgentRuntime`, so thread it via `AgentRuntime`'s ctor: add a `SystemPromptStore` parameter and pass through).
- Modify: `src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs` (ctor: accept `SystemPromptStore`, pass to `new PromptAssembler(...)`).

**Interfaces:**
- Consumes: `SystemPromptStore.Read()`, `SystemPromptStore.Fingerprint()`, `SystemPromptRenderer.Render`.
- Produces: `PromptAssembler` renders the main template; `VoiceMode`/`CodingRelay` come from config; a fingerprint is logged per build.

- [ ] **Step 1 (RED via existing guard):** Change `PromptAssembler` to build a placeholder
  dictionary and render. Remove the local `VoiceModeInstructions`/`CodingAgentRelayPrompt`
  usage in favor of `config.VoiceMode`/`config.CodingRelay` (delete the now-unused private
  `CodingAgentRelayPrompt` const; the `VoiceModeInstructions` const is superseded by
  `SystemPromptDefaults.VoiceMode` — delete it too). Add a `SystemPromptStore` field + ctor
  param (nullable to preserve the many test ctors; when null, use `SystemPromptDefaults.Create()`).

  In `BuildPromptAsync`, replace the `systemPrompt` concatenation (PromptAssembler.cs:216-276)
  with:

```csharp
var config = this.systemPromptStore?.Read() ?? Contracts.SystemPrompt.SystemPromptDefaults.Create();

var channelLabel = GetChannelLabel(channelId);
var channelValue = channelLabel is not null
    ? $"\nThe user is currently talking to you via {channelLabel}."
    : string.Empty;

var activeTasksValue = BuildActiveTasksSection();   // "" when none — see below
var activePlansValue = BuildActivePlansSection(session); // "" when none

var values = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["personality"] = personality,
    ["self_notes"] = selfNotes,
    ["skills"] = skillsSection,
    ["channel"] = channelValue,
    ["voice_mode"] = isVoice ? config.VoiceMode : string.Empty,
    ["active_tasks"] = activeTasksValue,
    ["active_plans"] = activePlansValue,
    ["coding_relay"] = config.CodingRelay,
};

var systemPrompt = SystemPromptRenderer.Render(config.MainTemplate, values);
```

  Extract the existing active-tasks and active-plans building blocks (PromptAssembler.cs:246-273)
  into `private string BuildActiveTasksSection()` and
  `private string BuildActivePlansSection(AgentSession session)` returning the SAME strings as
  today (including the leading `"\n\n## Active background tasks\n"` etc.), or `""` when empty.
  Keep the `## Self-notes` header inside the template (it is), so `values["self_notes"]` is the
  raw notes only — matching today where the header was a literal in the concat.

  Add fingerprint telemetry: after computing `systemPrompt`, call a new
  `[LoggerMessage]` `LogPromptFingerprint(conversationId, fingerprint)` with
  `this.systemPromptStore?.Fingerprint() ?? "default"`.

- [ ] **Step 2: Wire the store (Program.cs + AgentRuntime ctor).**

  In `Program.cs`, register the store near the other agent stores (search for where
  `SelfNotesStore` is registered) using the sandbox data path:

```csharp
builder.Services.AddSingleton(sp =>
    new SystemPromptStore(
        Path.Combine(sandboxRoot, "system-prompt.json"),
        sp.GetRequiredService<ILogger<SystemPromptStore>>()));
```

  Add `SystemPromptStore systemPromptStore` to the `AgentRuntime` ctor (after `selfNotesStore`,
  nullable default `= null` to preserve test ctors) and pass it into the `new PromptAssembler(...)`
  call (PromptAssembler is constructed at AgentRuntime.cs:211). Update the `Program.cs`
  `AgentRuntime` construction to resolve and pass `SystemPromptStore`.

- [ ] **Step 3: Run the characterization + unit tests**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptCharacterizationTests|ClassName=SystemPromptRendererTests"`
Expected: PASS — main-prompt golden unchanged.

- [ ] **Step 4: Build the whole solution**

Run: `dotnet build cortex-contained.sln`
Expected: SUCCESS, no warnings-as-errors.

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Agent/PromptAssembler.cs src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs src/Cortex.Contained.Agent.Host/Program.cs
git commit -m "refactor(agent): PromptAssembler renders main template from SystemPromptStore + fingerprint telemetry"
```

---

### Task 7: Refactor SubAgentStartTool subagent prompt to render from store

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SubAgentStartTool.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (pass `SystemPromptStore` into `SubAgentStartTool`).

**Interfaces:**
- Consumes: `SystemPromptStore.Read()`, `SystemPromptRenderer.Render`, `SkillRegistry`.
- Produces: `BuildSubagentSystemPrompt` renders `config.SubagentTemplate`; `{{instructions}}` = `config.SubagentInstructions`.

- [ ] **Step 1: Refactor `BuildSubagentSystemPrompt` (SubAgentStartTool.cs:560-605).**

  Add a `SystemPromptStore? systemPromptStore` ctor param (nullable, default null; when null use
  defaults) and field. Replace the method body with a placeholder-dictionary render. Build each
  value to match today EXACTLY:
  - `skill` = when `skillName` resolves via `skillRegistry.ReadSkillContent`, the string
    `$"## Skill: {skillName}\n\n{skillContent}\n\n"`; else `""`. (This reproduces the current
    header + blank lines.)
  - `instructions` = `config.SubagentInstructions` (the six lines).
  - `skills` = `this.skillRegistry?.FormatForSystemPrompt() ?? ""`.
  - `bootstrap_context` = when present, `$"\n## User context\n{bootstrapContext}"`; else `""`.
    (Match current spacing at SubAgentStartTool.cs:590-595.)
  - `recalled_memories` = when present, `$"\n## Recalled context\n{memories}"`; else `""`.
  - `personality` = `""` (persona out by default; the value is available if a future opt-in is added).

```csharp
private string BuildSubagentSystemPrompt(string memories, string? bootstrapContext, string? skillName = null)
{
    var config = this.systemPromptStore?.Read() ?? Contracts.SystemPrompt.SystemPromptDefaults.Create();

    var skillValue = string.Empty;
    if (skillName is not null && this.skillRegistry is not null)
    {
        var skillContent = this.skillRegistry.ReadSkillContent(skillName);
        if (skillContent is not null)
        {
            skillValue = $"## Skill: {skillName}\n\n{skillContent}\n\n";
        }
    }

    var values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["personality"] = string.Empty,
        ["skill"] = skillValue,
        ["instructions"] = config.SubagentInstructions,
        ["skills"] = this.skillRegistry?.FormatForSystemPrompt() ?? string.Empty,
        ["bootstrap_context"] = string.IsNullOrWhiteSpace(bootstrapContext)
            ? string.Empty
            : $"\n## User context\n{bootstrapContext}",
        ["recalled_memories"] = string.IsNullOrWhiteSpace(memories)
            ? string.Empty
            : $"\n## Recalled context\n{memories}",
    };

    return SystemPromptRenderer.Render(config.SubagentTemplate, values);
}
```

  > Careful: today's method uses `AppendLine` which emits platform newlines and a specific
  > order (skill → instructions → skills → bootstrap → memories). The default `SubagentTemplate`
  > places `{{personality}}{{skill}}{{instructions}}{{skills}}{{bootstrap_context}}{{recalled_memories}}`.
  > Because `AppendLine` on the existing code produced `\n`-terminated lines, verify the Task-5
  > subagent characterization string matches; adjust the value strings' trailing/leading `\n`
  > until the golden passes. The `SubagentInstructions` default must therefore end with a
  > trailing `\n` (each line was `AppendLine`d) — ensure Task 1's paste reflects that.

- [ ] **Step 2: Wire the store into SubAgentStartTool (Program.cs:488-502).** Add the
  `SystemPromptStore` argument (resolve `sp.GetRequiredService<SystemPromptStore>()`) as a new
  trailing constructor argument.

- [ ] **Step 3: Run subagent characterization + full agent tests**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptCharacterizationTests"`
Expected: PASS — subagent golden unchanged.

- [ ] **Step 4: Build**

Run: `dotnet build cortex-contained.sln`
Expected: SUCCESS.

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SubAgentStartTool.cs src/Cortex.Contained.Agent.Host/Program.cs
git commit -m "refactor(agent): subagent prompt renders SubagentTemplate from SystemPromptStore"
```

---

### Task 8: Runtime API + hub methods (get/set/reset/preview) + audit telemetry

**Files:**
- Modify: `src/Cortex.Contained.Contracts/Hub/IChatHub.cs` (add 4 methods)
- Modify: `src/Cortex.Contained.Agent.Host/Agent/IAgentRuntime.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/SystemPromptRuntimeTests.cs`

**Interfaces:**
- Produces on `IChatHub` (and therefore `IAgentHub`):
  - `Task<SystemPromptConfig> GetSystemPromptConfig();`
  - `Task<SystemPromptValidationResult> SetSystemPromptConfig(SystemPromptConfig config);`
  - `Task<SystemPromptConfig> ResetSystemPromptConfig();`
  - `Task<string> GetSystemPromptPreview(string channelId, bool isVoice);`
- Produces on `IAgentRuntime`: same four as `...Async(..., CancellationToken)`.
- `AgentRuntime` delegates to `SystemPromptStore`; `SetSystemPromptConfigAsync` logs an audit line
  (changed fields, old→new fingerprint, warnings). Preview renders the main template with live
  computed values plus sample text for conditional blocks.

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Agent.Host.Tests;

public class SystemPromptRuntimeTests
{
    // Construct AgentRuntime via its existing test ctor/helper used by other tests in this
    // project (mirror SessionManagementTests' setup); inject a SystemPromptStore over a temp dir.

    [Fact]
    public async Task SetThenGet_RoundTripsConfig()
    {
        var runtime = TestRuntime.Create(out _);
        var config = SystemPromptDefaults.Create();
        config.CodingRelay = "relay-x";

        var result = await runtime.SetSystemPromptConfigAsync(config, CancellationToken.None);
        Assert.True(result.IsValid);

        var read = await runtime.GetSystemPromptConfigAsync(CancellationToken.None);
        Assert.Equal("relay-x", read.CodingRelay);
    }

    [Fact]
    public async Task Set_Invalid_ReturnsErrorsAndDoesNotPersist()
    {
        var runtime = TestRuntime.Create(out _);
        var bad = SystemPromptDefaults.Create();
        bad.SubagentTemplate = "{{unknown}}";

        var result = await runtime.SetSystemPromptConfigAsync(bad, CancellationToken.None);

        Assert.False(result.IsValid);
        var read = await runtime.GetSystemPromptConfigAsync(CancellationToken.None);
        Assert.Equal(SystemPromptDefaults.SubagentTemplate, read.SubagentTemplate);
    }

    [Fact]
    public async Task Preview_ReturnsRenderedStringWithPersona()
    {
        var runtime = TestRuntime.Create(out _); // persona delegate returns "You are Test."
        var preview = await runtime.GetSystemPromptPreviewAsync("web", isVoice: false, CancellationToken.None);
        Assert.Contains("You are Test.", preview, StringComparison.Ordinal);
    }
}
```

  > If no `TestRuntime` helper exists, add a small internal static factory in the test project
  > that builds `AgentRuntime` the way `SessionManagementTests` already does, parameterized with
  > a `SystemPromptStore` over a temp dir and a persona delegate. Reuse existing substitutes.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptRuntimeTests"`
Expected: FAIL — methods not defined.

- [ ] **Step 3: Implement.**

  `IChatHub.cs` — add the four method signatures (with XML docs) alongside the personality ones.

  `IAgentRuntime.cs` — add:

```csharp
Task<Contracts.SystemPrompt.SystemPromptConfig> GetSystemPromptConfigAsync(CancellationToken cancellationToken);
Task<Contracts.SystemPrompt.SystemPromptValidationResult> SetSystemPromptConfigAsync(
    Contracts.SystemPrompt.SystemPromptConfig config, CancellationToken cancellationToken);
Task<Contracts.SystemPrompt.SystemPromptConfig> ResetSystemPromptConfigAsync(CancellationToken cancellationToken);
Task<string> GetSystemPromptPreviewAsync(string channelId, bool isVoice, CancellationToken cancellationToken);
```

  `AgentRuntime.cs` — implement near the personality region (AgentRuntime.cs:1623+). Use the
  injected `systemPromptStore`. For preview, resolve live values (personality via
  `LoadPersonality()`, self-notes via `selfNotesStore?.Read()`, skills via
  `skillRegistry?.FormatForSystemPrompt()`), use sample text for conditional blocks
  (`channel` from the channel label, `voice_mode` = config.VoiceMode when `isVoice`,
  `active_tasks`/`active_plans` = a short "(none)" sample or the real section), then
  `SystemPromptRenderer.Render(config.MainTemplate, values)`. Extract a shared
  `BuildMainPlaceholderValues(...)` helper reused by `PromptAssembler` if practical, or duplicate
  the small mapping. Audit log:

```csharp
[LoggerMessage(Level = LogLevel.Information,
    Message = "[system-prompt] config updated: changed={ChangedFields} {OldFingerprint}->{NewFingerprint} warnings={WarningCount}")]
private partial void LogSystemPromptUpdated(string changedFields, string oldFingerprint, string newFingerprint, int warningCount);
```

  Compute `changedFields` by comparing incoming config to the pre-write `Read()`.

  `AgentHub.cs` — add the four hub methods delegating to `this.runtime.*Async(..., Context.ConnectionAborted)`, mirroring `GetPersonality`.

- [ ] **Step 4: Run tests + build**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=SystemPromptRuntimeTests"` → PASS
Run: `dotnet build cortex-contained.sln` → SUCCESS

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Contracts/Hub/IChatHub.cs src/Cortex.Contained.Agent.Host/Agent/IAgentRuntime.cs src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs tests/Cortex.Contained.Agent.Host.Tests/SystemPromptRuntimeTests.cs
git commit -m "feat(agent): system-prompt get/set/reset/preview runtime API + hub methods + audit log"
```

---

### Task 9: Bridge — HubClient methods + REST endpoints

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Hub/HubClient.cs`
- Modify: `src/Cortex.Contained.Bridge/Tenants/TenantEndpoints.cs`

**Interfaces:**
- Consumes: `IAgentHub.GetSystemPromptConfig/SetSystemPromptConfig/ResetSystemPromptConfig/GetSystemPromptPreview`.
- Produces on `HubClient`:
  - `Task<SystemPromptConfig> GetSystemPromptConfigAsync(CancellationToken)`
  - `Task<SystemPromptValidationResult> SetSystemPromptConfigAsync(SystemPromptConfig, CancellationToken)`
  - `Task<SystemPromptConfig> ResetSystemPromptConfigAsync(CancellationToken)`
  - `Task<string> GetSystemPromptPreviewAsync(string channelId, bool isVoice, CancellationToken)`
- REST (mirror personality endpoints, same auth):
  - `GET /api/tenants/{tenantId}/system-prompt` → `SystemPromptConfig`
  - `PUT /api/tenants/{tenantId}/system-prompt` → `{ ok, warnings }` (400 + `{ errors }` when invalid)
  - `DELETE /api/tenants/{tenantId}/system-prompt` → reset config
  - `GET /api/tenants/{tenantId}/system-prompt/preview?channel=&voice=` → `{ preview }`

- [ ] **Step 1: HubClient methods.** Mirror `GetPersonalityAsync` (HubClient.cs:288). Example:

```csharp
public async Task<SystemPromptConfig> GetSystemPromptConfigAsync(CancellationToken cancellationToken)
{
    EnsureConnected();
    return await this.connection!.InvokeAsync<SystemPromptConfig>(
        nameof(IAgentHub.GetSystemPromptConfig), cancellationToken).ConfigureAwait(false);
}

public async Task<SystemPromptValidationResult> SetSystemPromptConfigAsync(
    SystemPromptConfig config, CancellationToken cancellationToken)
{
    EnsureConnected();
    return await this.connection!.InvokeAsync<SystemPromptValidationResult>(
        nameof(IAgentHub.SetSystemPromptConfig), config, cancellationToken).ConfigureAwait(false);
}

public async Task<SystemPromptConfig> ResetSystemPromptConfigAsync(CancellationToken cancellationToken)
{
    EnsureConnected();
    return await this.connection!.InvokeAsync<SystemPromptConfig>(
        nameof(IAgentHub.ResetSystemPromptConfig), cancellationToken).ConfigureAwait(false);
}

public async Task<string> GetSystemPromptPreviewAsync(string channelId, bool isVoice, CancellationToken cancellationToken)
{
    EnsureConnected();
    return await this.connection!.InvokeAsync<string>(
        nameof(IAgentHub.GetSystemPromptPreview), channelId, isVoice, cancellationToken).ConfigureAwait(false);
}
```

  Add `using Cortex.Contained.Contracts.SystemPrompt;` to HubClient.cs.

- [ ] **Step 2: REST endpoints.** In `TenantEndpoints.cs`, after the personality block
  (TenantEndpoints.cs:216-280), add the four endpoints. Reuse the exact client-resolution +
  auth pattern used by the personality endpoints (copy the surrounding `client` acquisition).
  PUT reads `SystemPromptConfig` from the body, calls `SetSystemPromptConfigAsync`, and returns
  `Results.BadRequest(new { errors = result.Errors })` when `!result.IsValid`, else
  `Results.Ok(new { ok = true, warnings = result.Warnings })`.

- [ ] **Step 3: Build**

Run: `dotnet build cortex-contained.sln`
Expected: SUCCESS.

- [ ] **Step 4: Manual endpoint smoke (optional but recommended)** — with the stack running
  (`.\scripts\Start-Cortex.ps1`), `GET /api/tenants/admin/system-prompt` returns defaults; a
  `PUT` with `{{bogus}}` returns 400 with an `errors` array.

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Bridge/Hub/HubClient.cs src/Cortex.Contained.Bridge/Tenants/TenantEndpoints.cs
git commit -m "feat(bridge): system-prompt REST endpoints + hub client methods"
```

---

### Task 10: Web UI — System Prompt editor with live preview

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-settings.js`
- Modify: `src/Cortex.Contained.Bridge/wwwroot/app.html` (add the System Prompt card markup near the personality editor)

**Interfaces:**
- Consumes: the REST endpoints from Task 9.
- Produces: an editor card with two template textareas + three segment textareas, placeholder
  chips (click-to-insert), reset-to-default per field (or whole config), a debounced live
  preview pane, and inline error/warning display.

- [ ] **Step 1: Add Alpine state + methods to `tenant-settings.js`** (mirror the personality
  block at tenant-settings.js:145-191). State:

```javascript
systemPrompt: { mainTemplate: "", subagentTemplate: "", voiceMode: "", codingRelay: "", subagentInstructions: "" },
systemPromptLoading: false,
savingSystemPrompt: false,
systemPromptErrors: [],
systemPromptWarnings: [],
systemPromptPreview: "",
```

  Methods `loadSystemPrompt()`, `saveSystemPrompt()`, `resetSystemPrompt()`,
  `refreshPreview()` (debounced), `insertPlaceholder(field, name)`. `saveSystemPrompt` PUTs the
  `systemPrompt` object; on 400 populate `systemPromptErrors` from `err.body.errors`; on success
  set `systemPromptWarnings` from the response. Add `this.loadSystemPrompt()` to the existing
  `Promise.all([...])` in the init (tenant-settings.js:68).

```javascript
async loadSystemPrompt() {
    this.systemPromptLoading = true;
    try {
        const data = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/system-prompt`);
        this.systemPrompt = data;
        await this.refreshPreview();
    } catch (e) {
        Alpine.store("toast").error("Failed to load system prompt");
    }
    this.systemPromptLoading = false;
},
async saveSystemPrompt() {
    this.savingSystemPrompt = true;
    this.systemPromptErrors = [];
    try {
        const res = await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}/system-prompt`, this.systemPrompt);
        this.systemPromptWarnings = res.warnings || [];
        Alpine.store("toast").success("System prompt saved");
        await this.refreshPreview();
    } catch (e) {
        this.systemPromptErrors = (e && e.body && e.body.errors) || ["Save failed"];
    }
    this.savingSystemPrompt = false;
},
async resetSystemPrompt() {
    this.savingSystemPrompt = true;
    try {
        this.systemPrompt = await api.del(`/api/tenants/${encodeURIComponent(this.tenantId)}/system-prompt`);
        this.systemPromptErrors = [];
        this.systemPromptWarnings = [];
        Alpine.store("toast").success("System prompt reset to default");
        await this.refreshPreview();
    } catch (e) {
        Alpine.store("toast").error("Reset failed");
    }
    this.savingSystemPrompt = false;
},
async refreshPreview() {
    try {
        const data = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/system-prompt/preview?channel=web&voice=false`);
        this.systemPromptPreview = data.preview || "";
    } catch (e) { /* preview is best-effort */ }
},
insertPlaceholder(field, name) {
    this.systemPrompt[field] = (this.systemPrompt[field] || "") + "{{" + name + "}}";
},
```

  > `api.del` currently returns JSON for personality reset; confirm it parses the body. If `api`
  > lacks a helper, follow whatever the personality reset uses (tenant-settings.js:184).

- [ ] **Step 2: Add the card markup to `app.html`** near the personality editor. Include:
  labelled textareas bound with `x-model` to `systemPrompt.mainTemplate` etc.; a chip row per
  template rendering the allowed placeholder names (hardcode the two lists to match
  `SystemPromptPlaceholders`) each calling `insertPlaceholder`; Save/Reset buttons bound to
  `saveSystemPrompt`/`resetSystemPrompt` and disabled while `savingSystemPrompt`; an errors block
  (`x-for` over `systemPromptErrors`, red) and warnings block (amber); and a read-only preview
  `<pre x-text="systemPromptPreview">`. Follow the existing card/markup classes used by the
  personality section for visual consistency.

- [ ] **Step 3: Manual verify** — run `.\scripts\Start-Cortex.ps1 -BridgeOnly` (agent already
  running), open the tenant settings page, confirm: load shows defaults; editing a template +
  Save updates the preview; entering `{{bogus}}` + Save shows a red error and does not persist;
  Reset restores defaults.

- [ ] **Step 4: Commit**

```bash
git add src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-settings.js src/Cortex.Contained.Bridge/wwwroot/app.html
git commit -m "feat(bridge-ui): system-prompt editor with placeholder chips, validation, live preview"
```

---

### Task 11: Full verification

**Files:** none (verification only)

- [ ] **Step 1: Full test suite**

Run: `dotnet test cortex-contained.sln`
Expected: all green, including the characterization/golden tests.

- [ ] **Step 2: Full build**

Run: `dotnet build cortex-contained.sln`
Expected: SUCCESS, zero warnings.

- [ ] **Step 3: End-to-end smoke** (use the `/run` skill or `.\scripts\Start-Cortex.ps1`):
  - Default install: send a normal message; confirm the agent replies (default prompt intact).
  - Edit the coding-relay block via UI, Save, send a message; confirm no tool breakage and the
    `[system-prompt] config updated` audit line + a `[system-prompt]` fingerprint line appear in
    the agent container log.
  - Start a subagent (`sub_agent`), confirm it still runs with the (default) subagent template.

- [ ] **Step 4: Update docs** — add a short "System Prompt customization" subsection to
  `docs/personality-architecture.md` pointing at the templates, placeholders, endpoints, and the
  `system-prompt.json` store location. Commit.

```bash
git add docs/personality-architecture.md
git commit -m "docs: system-prompt customization (templates, placeholders, endpoints)"
```

---

## Self-Review Notes

- **Spec coverage:** model (Task 1), renderer (2), validator (3), store (4), behavior-preservation (5–7), API+telemetry (8), Bridge (9), UI+preview (10), security caps/atomic-write/auth (Tasks 3/4/9), verification (11). All spec sections mapped.
- **Type consistency:** `SystemPromptConfig`, `SystemPromptValidationResult`, `SystemPromptPlaceholders`, `SystemPromptDefaults`, `SystemPromptRenderer.Render`, `SystemPromptStore.{Read,Write,Reset,Fingerprint}`, and the `*Async` runtime/hub/client method names are used identically across tasks.
- **Deferred (out of scope, per spec):** utility prompts; 2-level subagents (separate later spec).
```
