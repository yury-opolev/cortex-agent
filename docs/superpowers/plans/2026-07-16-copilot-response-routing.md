# Copilot Response Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Cortex and its bundled Coda support Copilot models such as `gpt-5.6-sol` by routing from live `supported_endpoints` metadata and implementing the Responses protocol.

**Architecture:** The Bridge preserves Copilot endpoint metadata and pushes it through the existing credential contract. The Agent Host resolves Responses -> Messages -> Chat per model while keeping `DirectLlmClient` responsible for retries/failover and provider clients responsible for wire protocols. The Coda submodule advances to the already-shipped Coda v0.1.64 fix.

**Tech Stack:** C# 14 / .NET 10, System.Text.Json, HttpClient/SSE, xUnit, NSubstitute, Git submodules, Docker, signed MSIX.

---

## File Structure

**Create**

- `src/Cortex.Contained.Agent.Host/Llm/Providers/Copilot/CopilotEndpoint.cs` - endpoint enum.
- `src/Cortex.Contained.Agent.Host/Llm/Providers/Copilot/CopilotEndpointResolver.cs` - metadata-only endpoint selection.
- `src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiResponsesRequest.cs` - Responses request DTO and mapping.
- `src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiResponsesResponse.cs` - non-streaming response DTOs and mapping helpers.
- `src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiResponsesSseReader.cs` - streaming event parser.
- `tests/Cortex.Contained.Agent.Host.Tests/CopilotEndpointResolverTests.cs`
- `tests/Cortex.Contained.Agent.Host.Tests/OpenAiResponsesRequestTests.cs`
- `tests/Cortex.Contained.Agent.Host.Tests/OpenAiResponsesParserTests.cs`
- `tests/Cortex.Contained.Bridge.Tests/CopilotEndpointMetadataTests.cs`

**Modify**

- `src/Cortex.Contained.Bridge/SetupHelpers.cs`
- `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs`
- `src/Cortex.Contained.Contracts/Hub/HubTypes.cs`
- `src/Cortex.Contained.Bridge/Setup/CortexConfigMutator.cs`
- `src/Cortex.Contained.Bridge/Setup/BridgeSettingsWriter.cs`
- `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs`
- `src/Cortex.Contained.Agent.Host/Llm/ProviderState.cs`
- `src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiCompatibleApiClient.cs`
- `src/Cortex.Contained.Agent.Host/Llm/Providers/Anthropic/AnthropicApiClient.cs`
- `src/Cortex.Contained.Agent.Host/Llm/DirectLlmClient.cs`
- `tests/Cortex.Contained.Agent.Host.Tests/OpenAiCompatibleApiClientTests.cs`
- `tests/Cortex.Contained.Bridge.Tests/Hosting/CredentialsPusherCopilotBearerTests.cs`
- `lib/coda-cli`

### Task 1: Preserve Copilot endpoint metadata in the Bridge

**Files:**
- Modify: `src/Cortex.Contained.Bridge/SetupHelpers.cs`
- Modify: `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs`
- Modify: `src/Cortex.Contained.Bridge/Setup/CortexConfigMutator.cs`
- Modify: `src/Cortex.Contained.Bridge/Setup/BridgeSettingsWriter.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/CopilotEndpointMetadataTests.cs`

- [ ] **Step 1: Write the failing `/models` parsing test**

Create a test that feeds:

```json
{
  "data": [{
    "id": "gpt-5.6-sol",
    "name": "GPT-5.6 Sol",
    "supported_endpoints": ["/responses", "ws:/responses"],
    "capabilities": {
      "type": "chat",
      "limits": {
        "max_context_window_tokens": 1050000,
        "max_output_tokens": 128000
      }
    }
  }]
}
```

Assert the returned `AvailableModel` has:

```csharp
Assert.Equal(["/responses", "ws:/responses"], model.SupportedEndpoints);
```

- [ ] **Step 2: Run the parsing test and verify RED**

Run:

```powershell
dotnet test tests/Cortex.Contained.Bridge.Tests --filter "Name~FetchCopilotApiModels_PreservesSupportedEndpoints"
```

Expected: compile failure because `SupportedEndpoints` does not exist.

- [ ] **Step 3: Add endpoint fields and mapping**

Add to `AvailableModel`, `CopilotModelEntry`, and `LlmModelDefinition`:

```csharp
public List<string> SupportedEndpoints { get; set; } = [];
```

Map the live entry:

```csharp
SupportedEndpoints = e.SupportedEndpoints?
    .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList() ?? [],
```

Add `[JsonPropertyName("supported_endpoints")]` to the Copilot DTO property.

- [ ] **Step 4: Persist endpoint lists in both YAML writers**

For non-empty lists write:

```yaml
supportedEndpoints:
  - /responses
  - ws:/responses
```

Use a `YamlSequenceNode` in `CortexConfigMutator` and indented lines in
`BridgeSettingsWriter`.

- [ ] **Step 5: Run Bridge metadata tests and verify GREEN**

Run:

```powershell
dotnet test tests/Cortex.Contained.Bridge.Tests --filter "Name~CopilotEndpointMetadata"
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/Cortex.Contained.Bridge/SetupHelpers.cs `
  src/Cortex.Contained.Contracts/Config/BridgeConfig.cs `
  src/Cortex.Contained.Bridge/Setup/CortexConfigMutator.cs `
  src/Cortex.Contained.Bridge/Setup/BridgeSettingsWriter.cs `
  tests/Cortex.Contained.Bridge.Tests/CopilotEndpointMetadataTests.cs
git commit -m "feat(copilot): preserve model endpoint metadata"
```

### Task 2: Push endpoint metadata to the Agent Host

**Files:**
- Modify: `src/Cortex.Contained.Contracts/Hub/HubTypes.cs`
- Modify: `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Llm/ProviderState.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/Hosting/CredentialsPusherCopilotBearerTests.cs`

- [ ] **Step 1: Write the failing credential propagation test**

Configure a Copilot `LlmModelDefinition` with:

```csharp
SupportedEndpoints = ["/responses"]
```

Assert the pushed `LlmModelMetadata` contains the same list.

- [ ] **Step 2: Run the propagation test and verify RED**

Run:

```powershell
dotnet test tests/Cortex.Contained.Bridge.Tests --filter "Name~BuildCredentials_CarriesSupportedEndpoints"
```

Expected: compile failure on the missing contract property.

- [ ] **Step 3: Extend the contract and pusher**

Add to `LlmModelMetadata`:

```csharp
public IReadOnlyList<string> SupportedEndpoints { get; init; } = [];
```

Map in `CredentialsPusher`:

```csharp
SupportedEndpoints = d.SupportedEndpoints,
```

Add to `ProviderState`:

```csharp
public LlmModelMetadata? FindModelMetadata(string model) =>
    this.Credential.ModelMetadata?.FirstOrDefault(metadata =>
        string.Equals(metadata.Id, model, StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 4: Run the propagation tests and verify GREEN**

Run the test from Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Contracts/Hub/HubTypes.cs `
  src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs `
  src/Cortex.Contained.Agent.Host/Llm/ProviderState.cs `
  tests/Cortex.Contained.Bridge.Tests/Hosting/CredentialsPusherCopilotBearerTests.cs
git commit -m "feat(copilot): push endpoint metadata to agent"
```

### Task 3: Add deterministic endpoint resolution

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Llm/Providers/Copilot/CopilotEndpoint.cs`
- Create: `src/Cortex.Contained.Agent.Host/Llm/Providers/Copilot/CopilotEndpointResolver.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/CopilotEndpointResolverTests.cs`

- [ ] **Step 1: Write resolver tests**

Cover:

```csharp
[InlineData("/chat/completions,/responses", CopilotEndpoint.Responses)]
[InlineData("/v1/messages,/chat/completions", CopilotEndpoint.Messages)]
[InlineData("/chat/completions", CopilotEndpoint.ChatCompletions)]
[InlineData("", CopilotEndpoint.ChatCompletions)]
[InlineData("ws:/responses", CopilotEndpoint.ChatCompletions)]
```

- [ ] **Step 2: Run resolver tests and verify RED**

Run:

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=CopilotEndpointResolverTests"
```

Expected: compile failure because resolver types do not exist.

- [ ] **Step 3: Implement the resolver**

```csharp
public enum CopilotEndpoint
{
    ChatCompletions,
    Messages,
    Responses,
}

public static CopilotEndpoint Resolve(IReadOnlyList<string>? endpoints)
{
    endpoints ??= [];
    if (Contains(endpoints, "/responses") || Contains(endpoints, "/v1/responses"))
    {
        return CopilotEndpoint.Responses;
    }

    if (Contains(endpoints, "/v1/messages"))
    {
        return CopilotEndpoint.Messages;
    }

    return CopilotEndpoint.ChatCompletions;
}
```

Use case-insensitive equality; do not inspect model IDs.

- [ ] **Step 4: Run resolver tests and verify GREEN**

Expected: all resolver tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Llm/Providers/Copilot `
  tests/Cortex.Contained.Agent.Host.Tests/CopilotEndpointResolverTests.cs
git commit -m "feat(copilot): resolve model endpoint from metadata"
```

### Task 4: Map Cortex requests to the Responses protocol

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiResponsesRequest.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/OpenAiResponsesRequestTests.cs`

- [ ] **Step 1: Write request mapping tests**

Build a request containing:

- system instruction
- user text and image
- assistant text and `LlmToolCall`
- tool result
- one tool definition

Assert:

```csharp
Assert.Equal("gpt-5.6-sol", body.Model);
Assert.Equal("system text", body.Instructions);
Assert.Contains(body.Input, item => item.Type == "function_call");
Assert.Contains(body.Input, item => item.Type == "function_call_output");
Assert.Equal("function", Assert.Single(body.Tools!).Type);
```

- [ ] **Step 2: Run request tests and verify RED**

Run:

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=OpenAiResponsesRequestTests"
```

Expected: compile failure because the mapper does not exist.

- [ ] **Step 3: Implement focused DTOs and mapper**

Expose:

```csharp
internal static OpenAiResponsesRequest Build(LlmCompletionRequest request)
```

Map:

```csharp
system -> Instructions
user text -> { role = "user", content = [{ type = "input_text" }] }
image -> { type = "input_image", image_url = "data:<mime>;base64,<data>" }
assistant text -> { role = "assistant", content = [{ type = "output_text" }] }
tool call -> { type = "function_call", call_id, name, arguments }
tool result -> { type = "function_call_output", call_id, output }
```

Omit `max_output_tokens`, matching the Coda fix that avoids exhausting the
reasoning budget before visible output.

- [ ] **Step 4: Run request tests and verify GREEN**

Expected: all request mapping tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiResponsesRequest.cs `
  tests/Cortex.Contained.Agent.Host.Tests/OpenAiResponsesRequestTests.cs
git commit -m "feat(copilot): map requests to Responses API"
```

### Task 5: Parse non-streaming and streaming Responses output

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiResponsesResponse.cs`
- Create: `src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiResponsesSseReader.cs`
- Create: `tests/Cortex.Contained.Agent.Host.Tests/OpenAiResponsesParserTests.cs`

- [ ] **Step 1: Write non-streaming parser tests**

Use a response containing assistant `output_text`, one `function_call`, and
usage. Assert `LlmCompletionResult` text, tool name/arguments, finish reason,
and token usage.

- [ ] **Step 2: Write streaming parser tests**

Cover:

- `response.output_text.delta`
- tool call added + argument deltas + done
- `response.completed`
- `response.incomplete` with `max_output_tokens`
- `response.failed` with `response.error.message`
- EOF without terminal event

- [ ] **Step 3: Run parser tests and verify RED**

Run:

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "ClassName=OpenAiResponsesParserTests"
```

Expected: compile failure because parser types do not exist.

- [ ] **Step 4: Implement parsers**

Non-streaming parser returns:

```csharp
new LlmCompletionResult
{
    Success = true,
    Content = joinedOutputText,
    ToolCalls = functionCalls,
    FinishReason = functionCalls.Count > 0 ? "tool_calls" : "stop",
    Usage = usage,
    ProviderId = provider.Credential.Name,
};
```

Streaming parser accumulates function arguments by `output_index`, emits
`LlmToolCallDelta` chunks, maps completion usage, and emits exactly one terminal
chunk. Throw `InvalidDataException` if the stream ends without a terminal event.

- [ ] **Step 5: Run parser tests and verify GREEN**

Expected: all parser tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiResponsesResponse.cs `
  src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiResponsesSseReader.cs `
  tests/Cortex.Contained.Agent.Host.Tests/OpenAiResponsesParserTests.cs
git commit -m "feat(copilot): parse Responses API output"
```

### Task 6: Integrate endpoint dispatch with retry and bearer refresh

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiCompatibleApiClient.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Llm/Providers/Anthropic/AnthropicApiClient.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Llm/DirectLlmClient.cs`
- Modify: `tests/Cortex.Contained.Agent.Host.Tests/OpenAiCompatibleApiClientTests.cs`

- [ ] **Step 1: Write failing direct-client endpoint tests**

For a Copilot provider whose model metadata advertises `["/responses"]`, assert:

```csharp
Assert.EndsWith("/responses", sent.RequestUri!.AbsolutePath);
```

Cover both `CompleteAsync` and `StreamAsync`, plus a 401 refresh retry that
uses `/responses` on both attempts.

Add a compatibility test where metadata is absent and the URL remains
`/chat/completions`.

- [ ] **Step 2: Run integration tests and verify RED**

Run:

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~GitHubCopilotResponses"
```

Expected: requests still target `/chat/completions`.

- [ ] **Step 3: Refactor endpoint selection without changing retry ownership**

At the start of each provider call:

```csharp
var endpoint = provider.Credential.Api == "github-copilot-api"
    ? CopilotEndpointResolver.Resolve(
        provider.FindModelMetadata(request.Model)?.SupportedEndpoints)
    : CopilotEndpoint.ChatCompletions;
```

Use one selected endpoint and serialized body for the initial request and 401
retry. Responses requests go to `responses`; Chat stays on existing paths.

- [ ] **Step 4: Wire non-streaming Responses parsing**

On success:

```csharp
return endpoint == CopilotEndpoint.Responses
    ? OpenAiResponsesResponse.Parse(responseJson, provider)
    : ParseChatCompletion(responseJson, provider);
```

- [ ] **Step 5: Wire streaming Responses parsing**

Select the parser once:

```csharp
var chunks = endpoint == CopilotEndpoint.Responses
    ? OpenAiResponsesSseReader.ReadAsync(reader, provider, cancellationToken)
    : this.ParseOpenAiSseAsync(reader, request.RequestId, cancellationToken);
```

Reuse the same selection in the 401 retry branch.

- [ ] **Step 6: Support Copilot Messages metadata**

Extend `AnthropicApiClient` request authentication:

```csharp
if (provider.Credential.Api == "github-copilot-api")
{
    request.Headers.Authorization =
        new AuthenticationHeaderValue("Bearer", provider.CurrentAccessToken
            ?? provider.Credential.AccessToken);
    request.Headers.Add("Openai-Intent", "conversation-edits");
    request.Headers.Add("x-initiator", "user");
    request.Headers.Add("X-GitHub-Api-Version", "2026-06-01");
    return;
}
```

Dispatch Messages-selected Copilot requests to `AnthropicApiClient` from
`DirectLlmClient` while leaving provider retry/failover around the call.

- [ ] **Step 7: Run endpoint, retry, and failover tests**

Run:

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Name~Copilot|Name~Retry|Name~Failover"
```

Expected: all selected tests pass.

- [ ] **Step 8: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Llm/Providers/OpenAi/OpenAiCompatibleApiClient.cs `
  src/Cortex.Contained.Agent.Host/Llm/Providers/Anthropic/AnthropicApiClient.cs `
  src/Cortex.Contained.Agent.Host/Llm/DirectLlmClient.cs `
  tests/Cortex.Contained.Agent.Host.Tests/OpenAiCompatibleApiClientTests.cs
git commit -m "fix(copilot): route direct calls by model metadata"
```

### Task 7: Advance the bundled Coda submodule

**Files:**
- Modify: `lib/coda-cli`

- [ ] **Step 1: Confirm the current pin is pre-fix**

Run:

```powershell
git submodule status lib/coda-cli
```

Expected: `6650275... lib/coda-cli`.

- [ ] **Step 2: Fetch and checkout Coda v0.1.64**

Run:

```powershell
git -C lib/coda-cli fetch origin
git -C lib/coda-cli checkout a1f5398043ee7b2575c4fe282c74244c18e63fa5
```

- [ ] **Step 3: Verify the pin**

Run:

```powershell
git submodule status lib/coda-cli
```

Expected: `a1f5398... lib/coda-cli`.

- [ ] **Step 4: Commit**

```powershell
git add lib/coda-cli
git commit -m "chore(coda): update bundled CLI to 0.1.64"
```

### Task 8: Full verification and highest-quality review

**Files:**
- Review all branch changes.

- [ ] **Step 1: Run the full solution tests**

Run:

```powershell
dotnet test cortex-contained.sln
```

Expected: zero failed tests and zero warnings.

- [ ] **Step 2: Run a full build**

Run:

```powershell
dotnet build cortex-contained.sln -c Release
```

Expected: build succeeds with zero warnings.

- [ ] **Step 3: Run Opus code review**

Dispatch the code-review agent with model `claude-opus-4.8`, maximum reasoning,
the design/spec paths, and `origin/main...HEAD`. Fix every Critical and Important
finding with new red/green tests.

- [ ] **Step 4: Re-run full tests after review fixes**

Run the commands from Steps 1 and 2 again.

- [ ] **Step 5: Push the branch**

```powershell
git push -u origin copilot-response-routing
```

### Task 9: Build and force-upgrade the local Cortex installation

**Files:**
- Generated: `artifacts/CortexLauncher-<version>.msix`
- Generated: Docker images and `artifacts/update-manifest.json`

- [ ] **Step 1: Build signed artifacts**

Run:

```powershell
.\scripts\Build-All.ps1 -CertThumbprint F578A5879BE57511D40288B6DA3A0F383BD74EEE
```

Record the bumped version from `version.json`.

- [ ] **Step 2: Verify generated artifacts**

Confirm:

```powershell
Get-AuthenticodeSignature .\artifacts\CortexLauncher-<version>.msix
Get-Content .\artifacts\update-manifest.json
```

Expected: signature status `Valid`, manifest version matches.

- [ ] **Step 3: Force-install the MSIX**

Run:

```powershell
Add-AppxPackage -Path .\artifacts\CortexLauncher-<version>.msix `
  -ForceUpdateFromAnyVersion `
  -ForceApplicationShutdown
```

- [ ] **Step 4: Relaunch the packaged launcher**

Run:

```powershell
Start-Process "shell:AppsFolder\Cortex.Contained.Launcher_hnfrhv5dkzjbe!CortexLauncher"
```

- [ ] **Step 5: Poll Bridge health**

Poll `http://localhost:5080/health` until:

```json
{ "healthy": true, "version": "<version>.0" }
```

- [ ] **Step 6: Verify the running Agent Host image**

Compare:

```powershell
docker image inspect cortex-agent:latest --format '{{.Id}}'
docker inspect cortex-agent --format '{{.Image}}'
```

Expected: IDs match.

- [ ] **Step 7: Verify bundled Coda and live model routing**

Use the Bridge/Cortex coding path to confirm bundled Coda reports v0.1.64, then
run a harmless live `gpt-5.6-sol` prompt through the main Cortex agent and a
tool-call prompt through the coding path.

- [ ] **Step 8: Store the shipped state in project memory**

Record Cortex version, main/branch commit, Coda submodule commit, test counts,
image ID, MSIX path, and health result.

