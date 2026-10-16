# Per-tenant Full LLM Provider Errors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a tenant opt into seeing the raw LLM provider error text in chat (default tenant on, others off), toggleable in `cortex.yml` and the web UI.

**Architecture:** The agent attaches the raw provider error to `AgentErrorMessage.Detail`; the Bridge's `HubMessageDispatcher` decides per tenant (via `TenantRouter.ShouldShowFullLlmErrors`) whether to append it, using a pure `LlmErrorDetailFormatter`. The flag lives on `TenantConfig` as `bool? ShowFullLlmErrors`, resolved as `ShowFullLlmErrors ?? Default`.

**Tech Stack:** .NET 10, xUnit + NSubstitute, SignalR contracts, Alpine.js web UI.

Spec: `docs/superpowers/specs/2026-10-16-tenant-full-llm-errors-design.md`

## Global Constraints

- Unset flag resolves to `true` for the default tenant, `false` for every other tenant; explicit value wins.
- Appended block format: `"{message}\n\nProvider response: {detail}"`.
- Detail capped at **1500** characters; when cut, append `"…"`.
- Agent's persisted history keeps the sanitized message only.
- C# style per `~/.claude/CLAUDE.md`: `this.` for instance members, braces always, `sealed`, file-scoped namespaces, one type per file, `ConfigureAwait(false)`.
- `TreatWarningsAsErrors` is on — build must be warning-free.
- Test naming: `Method_Condition_Expected`.

---

### Task 1: Contracts — tenant flag + error detail field

**Files:**
- Modify: `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs` (`TenantConfig`, after `VoiceGender`)
- Modify: `src/Cortex.Contained.Contracts/Hub/HubTypes.cs:135-144` (`AgentErrorMessage`)
- Test: `tests/Cortex.Contained.Contracts.Tests/TenantConfigShowFullLlmErrorsTests.cs` (create)

**Interfaces:**
- Produces: `TenantConfig.ShowFullLlmErrors : bool?`, `TenantConfig.ResolveShowFullLlmErrors() : bool`, `AgentErrorMessage.Detail : string?`

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Contracts.Tests;

public sealed class TenantConfigShowFullLlmErrorsTests
{
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void ResolveShowFullLlmErrors_DefaultAndExplicit_ResolvesExpected(bool isDefault, bool? explicitValue, bool expected)
    {
        var config = new TenantConfig { Default = isDefault, ShowFullLlmErrors = explicitValue };

        Assert.Equal(expected, config.ResolveShowFullLlmErrors());
    }

    [Fact]
    public void ShowFullLlmErrors_DefaultsToNull()
    {
        Assert.Null(new TenantConfig().ShowFullLlmErrors);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Contracts.Tests --filter "FullyQualifiedName~TenantConfigShowFullLlmErrorsTests"`
Expected: compile error — `ShowFullLlmErrors` not defined.

- [ ] **Step 3: Implement**

In `TenantConfig`, after `VoiceGender`:

```csharp
    /// <summary>
    /// Whether channel error messages for this tenant include the raw LLM provider
    /// response (e.g. <c>{"error":{"code":"model_not_supported",...}}</c>), capped for
    /// chat. Null = use the default: on for the default tenant, off for all others.
    /// Use <see cref="ResolveShowFullLlmErrors"/> to read the effective value.
    /// </summary>
    public bool? ShowFullLlmErrors { get; set; }

    /// <summary>Effective <see cref="ShowFullLlmErrors"/>: the explicit value, else <see cref="Default"/>.</summary>
    public bool ResolveShowFullLlmErrors() => this.ShowFullLlmErrors ?? this.Default;
```

In `AgentErrorMessage`, after `IsRetryable`:

```csharp
    /// <summary>
    /// Raw provider error text (e.g. <c>HTTP 400: {...}</c>) behind <see cref="Message"/>.
    /// The Bridge appends it to the channel message only for tenants that opt in.
    /// </summary>
    public string? Detail { get; init; }
```

- [ ] **Step 4: Run to verify it passes** — same command, expected PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Contracts tests/Cortex.Contained.Contracts.Tests/TenantConfigShowFullLlmErrorsTests.cs
git commit -m "feat(contracts): per-tenant ShowFullLlmErrors flag and AgentErrorMessage.Detail"
```

---

### Task 2: Agent — send raw provider error as Detail

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Agent/TurnResponseDelivery.cs:72-95`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Agent/TurnResponseDeliveryTests.cs` (add test)

**Interfaces:**
- Consumes: `AgentErrorMessage.Detail` (Task 1)

- [ ] **Step 1: Write the failing test** (add to `TurnResponseDeliveryTests`)

```csharp
    [Fact]
    public async Task PersistLlmErrorAsync_HttpError_SendsSanitizedMessageWithRawDetail()
    {
        var delivery = this.NewDelivery();
        const string raw = "HTTP 400: {\"error\":{\"code\":\"model_not_supported\"}}";

        await delivery.PersistLlmErrorAsync(raw, CancellationToken.None);

        await this.client.Received(1).OnError(Arg.Is<AgentErrorMessage>(m =>
            m.Message == "The model provider returned an error (HTTP 400)."
            && m.Detail == raw));
        var saved = await this.store.GetMessagesAsync("chan-1");
        Assert.DoesNotContain(saved, m => m.Content.Contains("model_not_supported", StringComparison.Ordinal));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~PersistLlmErrorAsync_HttpError_SendsSanitizedMessageWithRawDetail"`
Expected: FAIL (Detail is null).

- [ ] **Step 3: Implement** — in `PersistLlmErrorAsync`, add to the `AgentErrorMessage` initializer:

```csharp
            Detail = errorMessage,
```

and update the leading comment to: `// Channel gets a clean line; the raw text rides along as Detail so the Bridge can show it to tenants that opt in. History keeps the clean line only.`

- [ ] **Step 4: Run to verify it passes** — same command, PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Cortex.Contained.Agent.Host/Agent/TurnResponseDelivery.cs tests/Cortex.Contained.Agent.Host.Tests/Agent/TurnResponseDeliveryTests.cs
git commit -m "feat(agent): attach raw provider error as AgentErrorMessage.Detail"
```

---

### Task 3: Bridge — formatter, tenant resolution, dispatcher wiring

**Files:**
- Create: `src/Cortex.Contained.Bridge/Channels/LlmErrorDetailFormatter.cs`
- Modify: `src/Cortex.Contained.Bridge/Tenants/TenantRouter.cs` (add method after `IsConnected`)
- Modify: `src/Cortex.Contained.Bridge/Channels/HubMessageDispatcher.cs:330-336`
- Test: `tests/Cortex.Contained.Bridge.Tests/LlmErrorDetailFormatterTests.cs` (create)
- Test: `tests/Cortex.Contained.Bridge.Tests/TenantRouterTests.cs` (add tests)
- Test: `tests/Cortex.Contained.Bridge.Tests/HubMessageDispatcherTests.cs` (add tests)

**Interfaces:**
- Consumes: `TenantConfig.ResolveShowFullLlmErrors()`, `AgentErrorMessage.Detail` (Task 1)
- Produces: `LlmErrorDetailFormatter.Compose(string message, string? detail, bool showDetail) : string`, `LlmErrorDetailFormatter.MaxDetailLength = 1500`, `TenantRouter.ShouldShowFullLlmErrors(string tenantId) : bool`

- [ ] **Step 1: Write failing formatter tests**

```csharp
using Cortex.Contained.Bridge.Channels;

namespace Cortex.Contained.Bridge.Tests;

public sealed class LlmErrorDetailFormatterTests
{
    private const string Message = "The model provider returned an error (HTTP 400).";

    [Fact]
    public void Compose_ShowDetailFalse_ReturnsMessageOnly()
    {
        Assert.Equal(Message, LlmErrorDetailFormatter.Compose(Message, "HTTP 400: body", showDetail: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_NoDetail_ReturnsMessageOnly(string? detail)
    {
        Assert.Equal(Message, LlmErrorDetailFormatter.Compose(Message, detail, showDetail: true));
    }

    [Fact]
    public void Compose_ShowDetailWithDetail_AppendsProviderResponse()
    {
        var result = LlmErrorDetailFormatter.Compose(Message, "  HTTP 400: {\"code\":\"model_not_supported\"}  ", showDetail: true);

        Assert.Equal(Message + "\n\nProvider response: HTTP 400: {\"code\":\"model_not_supported\"}", result);
    }

    [Fact]
    public void Compose_LongDetail_TruncatesTo1500WithEllipsis()
    {
        var detail = new string('x', 4000);

        var result = LlmErrorDetailFormatter.Compose(Message, detail, showDetail: true);

        Assert.EndsWith(new string('x', LlmErrorDetailFormatter.MaxDetailLength) + "…", result);
        Assert.DoesNotContain(new string('x', LlmErrorDetailFormatter.MaxDetailLength + 1), result);
    }
}
```

- [ ] **Step 2: Add failing router tests** (in `TenantRouterTests`; build a router from a config with tenants):

```csharp
    [Theory]
    [InlineData("main", true)]
    [InlineData("guest", false)]
    [InlineData("unknown", false)]
    public void ShouldShowFullLlmErrors_UnsetFlag_FollowsDefaultTenant(string tenantId, bool expected)
    {
        var config = new BridgeConfig();
        config.Tenants["main"] = new TenantConfig { Default = true };
        config.Tenants["guest"] = new TenantConfig();
        var registry = new TenantRegistry(config, () => { }, NullLogger<TenantRegistry>.Instance);
        var router = new TenantRouter(registry, NullLoggerFactory.Instance, NullLogger<TenantRouter>.Instance);

        Assert.Equal(expected, router.ShouldShowFullLlmErrors(tenantId));
    }

    [Fact]
    public void ShouldShowFullLlmErrors_ExplicitFalseOnDefault_ReturnsFalse()
    {
        var config = new BridgeConfig();
        config.Tenants["main"] = new TenantConfig { Default = true, ShowFullLlmErrors = false };
        var registry = new TenantRegistry(config, () => { }, NullLogger<TenantRegistry>.Instance);
        var router = new TenantRouter(registry, NullLoggerFactory.Instance, NullLogger<TenantRouter>.Instance);

        Assert.False(router.ShouldShowFullLlmErrors("main"));
    }
```

(If `TenantRegistry` normalizes a config with no default tenant, this one has one — fine. Dispose routers with `await using` if the test class pattern requires; `TenantRouter` is `IAsyncDisposable` — use `await using var router = ...` and make the tests `async Task`.)

- [ ] **Step 3: Add failing dispatcher tests** (in `HubMessageDispatcherTests`, plus a reflection raiser mirroring `RaiseProactiveMessageEventOn`):

```csharp
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task OnError_TenantFlag_ControlsProviderDetail(bool isDefaultTenant, bool expectDetail)
    {
        var config = new BridgeConfig();
        config.Tenants["t1"] = new TenantConfig { Endpoint = "http://localhost:5100/t1", Enabled = true, Default = isDefaultTenant };
        var registry = new TenantRegistry(config, () => { }, NullLogger<TenantRegistry>.Instance);
        await using var router = new TenantRouter(registry, NullLoggerFactory.Instance, NullLogger<TenantRouter>.Instance);
        await using var channelManager = new ChannelManager(NullLogger<ChannelManager>.Instance);
        await using var client = new HubClient(NullLogger<HubClient>.Instance);
        var dispatcher = new HubMessageDispatcher(channelManager, router, NullLogger<HubMessageDispatcher>.Instance);
        dispatcher.Initialize();
        dispatcher.WireHubClient(client, "t1");
        var channel = CreateMockChannel("webchat-default");
        channel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });
        channelManager.RegisterChannel(channel);

        await RaiseErrorEventOn(client, new AgentErrorMessage
        {
            ConversationId = "webchat-default",
            ErrorCode = "LLM_ERROR",
            Message = "The model provider returned an error (HTTP 400).",
            Detail = "HTTP 400: model_not_supported",
        });

        await channel.Received(1).SendMessageAsync(
            Arg.Is<OutboundMessage>(m => m.Content.Text!.Contains("model_not_supported") == expectDetail),
            Arg.Any<CancellationToken>());
    }

    private static async Task RaiseErrorEventOn(HubClient client, AgentErrorMessage error)
    {
        var eventField = typeof(HubClient).GetField(
            nameof(HubClient.OnError),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(eventField);
        var handler = (Delegate)eventField!.GetValue(client)!;
        await (Task)handler.DynamicInvoke(error)!;
    }
```

(Use the real `ErrorCodes.LlmError` constant if it is accessible from Contracts; check `MessageContent.Text` nullability and drop the `!` if non-nullable.)

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~LlmErrorDetailFormatterTests|FullyQualifiedName~ShouldShowFullLlmErrors|FullyQualifiedName~OnError_TenantFlag"`
Expected: compile errors (formatter / router method missing).

- [ ] **Step 5: Implement formatter** — `src/Cortex.Contained.Bridge/Channels/LlmErrorDetailFormatter.cs`:

```csharp
namespace Cortex.Contained.Bridge.Channels;

/// <summary>
/// Composes the channel text for an agent error: the sanitized message, optionally
/// followed by the raw provider response for tenants that opt into full LLM errors.
/// The detail is capped so the whole message fits a Discord post (2000 chars).
/// </summary>
public static class LlmErrorDetailFormatter
{
    /// <summary>Maximum characters of provider detail shown in chat.</summary>
    public const int MaxDetailLength = 1500;

    public static string Compose(string message, string? detail, bool showDetail)
    {
        if (!showDetail || string.IsNullOrWhiteSpace(detail))
        {
            return message;
        }

        var trimmedDetail = detail.Trim();
        if (trimmedDetail.Length > MaxDetailLength)
        {
            trimmedDetail = string.Concat(trimmedDetail.AsSpan(0, MaxDetailLength), "…");
        }

        return $"{message}\n\nProvider response: {trimmedDetail}";
    }
}
```

- [ ] **Step 6: Implement router method** — in `TenantRouter` after `IsConnected`:

```csharp
    /// <summary>
    /// Whether channel errors for <paramref name="tenantId"/> should include the raw LLM
    /// provider response. Unknown tenants never do.
    /// </summary>
    public bool ShouldShowFullLlmErrors(string tenantId)
        => this.registry.GetTenant(tenantId)?.ResolveShowFullLlmErrors() ?? false;
```

- [ ] **Step 7: Wire dispatcher** — in `OnAgentErrorAsync`, replace `Content = new MessageContent { Text = agentError.Message },` with:

```csharp
                Content = new MessageContent
                {
                    Text = LlmErrorDetailFormatter.Compose(
                        agentError.Message,
                        agentError.Detail,
                        this.tenantRouter.ShouldShowFullLlmErrors(tenantId)),
                },
```

- [ ] **Step 8: Run to verify they pass** — same filter, all PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Cortex.Contained.Bridge/Channels src/Cortex.Contained.Bridge/Tenants/TenantRouter.cs tests/Cortex.Contained.Bridge.Tests
git commit -m "feat(bridge): append raw LLM provider error for tenants with ShowFullLlmErrors"
```

---

### Task 4: Bridge — persistence and tenant API

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Setup/BridgeSettingsWriter.cs:280-283` (after `apiEnabled`)
- Modify: `src/Cortex.Contained.Bridge/Tenants/TenantRouter.cs` (`TenantStatus` + `GetAllStatuses`)
- Modify: `src/Cortex.Contained.Bridge/Tenants/TenantEndpoints.cs` (GET projection ~line 52, POST ~line 84, PUT merge ~line 141, DTOs ~line 1323-1348)
- Test: `tests/Cortex.Contained.Bridge.Tests/TenantShowFullLlmErrorsYamlTests.cs` (create)

**Interfaces:**
- Consumes: `TenantConfig.ShowFullLlmErrors`, `ResolveShowFullLlmErrors()` (Task 1)
- Produces: JSON `showFullLlmErrors` (resolved bool) on `GET /api/tenants` items; `showFullLlmErrors` (bool?) accepted on `POST /api/tenants` and `PUT /api/tenants/{id}`

- [ ] **Step 1: Write failing YAML tests**

```csharp
using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests;

public sealed class TenantShowFullLlmErrorsYamlTests
{
    [Theory]
    [InlineData(true, "showFullLlmErrors: true")]
    [InlineData(false, "showFullLlmErrors: false")]
    public void PersistSettingsToYaml_ExplicitFlag_IsWritten(bool value, string expectedLine)
    {
        var yaml = WriteYaml(new TenantConfig { Endpoint = "http://127.0.0.1:5101/hub/agent", ShowFullLlmErrors = value });

        Assert.Contains(expectedLine, yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistSettingsToYaml_UnsetFlag_IsOmitted()
    {
        var yaml = WriteYaml(new TenantConfig { Endpoint = "http://127.0.0.1:5101/hub/agent" });

        Assert.DoesNotContain("showFullLlmErrors", yaml, StringComparison.Ordinal);
    }

    private static string WriteYaml(TenantConfig tenant)
    {
        var config = new BridgeConfig();
        config.Tenants["guest"] = tenant;
        var path = Path.Combine(Path.GetTempPath(), "cortex-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            BridgeSettingsWriter.PersistSettingsToYaml(config, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~TenantShowFullLlmErrorsYamlTests"`
Expected: 2 FAIL (explicit cases), 1 PASS.

- [ ] **Step 3: Implement writer** — after the `apiEnabled` block:

```csharp
                if (tenant.ShowFullLlmErrors is { } showFullLlmErrors)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    showFullLlmErrors: {(showFullLlmErrors ? "true" : "false")}");
                }
```

- [ ] **Step 4: Implement API surface**
  - `TenantStatus`: add `public bool ShowFullLlmErrors { get; init; }` under `// API channel` block with a `// Diagnostics` comment; in `GetAllStatuses` set `ShowFullLlmErrors = config.ResolveShowFullLlmErrors(),`.
  - GET projection: add `s.ShowFullLlmErrors,` after `s.ApiEnabled,`.
  - `CreateTenantRequest`: `public bool? ShowFullLlmErrors { get; init; }`; POST initializer: `ShowFullLlmErrors = request.ShowFullLlmErrors,`.
  - `UpdateTenantRequest`: `public bool? ShowFullLlmErrors { get; init; }`; PUT merge after `ApiEnabled`:

```csharp
            if (request.ShowFullLlmErrors.HasValue)
            {
                existing.ShowFullLlmErrors = request.ShowFullLlmErrors.Value;
            }
```

- [ ] **Step 5: Run to verify** — YAML tests PASS; `dotnet build cortex-contained.sln -v q` → 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Cortex.Contained.Bridge tests/Cortex.Contained.Bridge.Tests/TenantShowFullLlmErrorsYamlTests.cs
git commit -m "feat(bridge): persist and expose per-tenant showFullLlmErrors"
```

---

### Task 5: Web UI — Diagnostics toggle on tenant settings

**Files:**
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-settings.js` (state ~line 15, `load()` ~line 85, new `saveDiagnostics()`)
- Modify: `src/Cortex.Contained.Bridge/wwwroot/app.html` (new card right after the API Channel card, ~line 588)

**Interfaces:**
- Consumes: `showFullLlmErrors` on `GET /api/tenants` and `PUT /api/tenants/{id}` (Task 4)

- [ ] **Step 1: JS state + load + save**
  - State, after `apiEnabled: false,`: `showFullLlmErrors: false,`
  - In `load()`, after `this.apiEnabled = ...`: `this.showFullLlmErrors = this.tenant.showFullLlmErrors === true;`
  - New method after `save()` (separate save so toggling it does not resend unrelated fields):

```js
        async saveDiagnostics() {
            const intended = this.showFullLlmErrors;
            try {
                await api.put(`/api/tenants/${encodeURIComponent(this.tenantId)}`, {
                    showFullLlmErrors: intended,
                });
                Alpine.store("toast").success(intended
                    ? "Full LLM errors will be shown in chat"
                    : "Full LLM errors hidden from chat");
            } catch (e) {
                Alpine.store("toast").error(e.message);
                this.showFullLlmErrors = !intended;
            }
        },
```

- [ ] **Step 2: HTML card** — immediately before `<!-- API Channel Card -->`:

```html
                <!-- Diagnostics Card -->
                <div class="col-12">
                    <div class="card">
                        <div class="card-header"><i class="bi bi-bug me-2"></i>Diagnostics</div>
                        <div class="card-body">
                            <div class="form-check form-switch">
                                <input class="form-check-input" type="checkbox" id="full-llm-errors-toggle"
                                       x-model="showFullLlmErrors" @change="saveDiagnostics()">
                                <label class="form-check-label" for="full-llm-errors-toggle">Show full LLM provider errors in chat</label>
                            </div>
                            <small class="text-body-secondary">Appends the provider's raw error response (up to 1500 characters) to error messages. It can contain technical details. On by default for the default tenant only.</small>
                        </div>
                    </div>
                </div>

```

- [ ] **Step 3: Verify** — `dotnet build src/Cortex.Contained.Bridge -v q` (wwwroot is embedded/copied; must build clean). Then run the Bridge (`.\scripts\Start-Cortex.ps1 -BridgeOnly`), open tenant settings, toggle the switch, reload — state persists and `cortex.yml` shows `showFullLlmErrors:` for that tenant.

- [ ] **Step 4: Commit**

```bash
git add src/Cortex.Contained.Bridge/wwwroot
git commit -m "feat(ui): tenant Diagnostics toggle for full LLM provider errors"
```

---

### Task 6: Full verification

- [ ] `dotnet build cortex-contained.sln -v q --nologo` → 0 warnings, 0 errors.
- [ ] `dotnet test tests/Cortex.Contained.Contracts.Tests`, `tests/Cortex.Contained.Agent.Host.Tests`, `tests/Cortex.Contained.Bridge.Tests` → all pass.
- [ ] Update `docs/api-reference.md` tenant endpoints (add `showFullLlmErrors` to GET/POST/PUT field lists) and commit `docs: document showFullLlmErrors tenant field`.
