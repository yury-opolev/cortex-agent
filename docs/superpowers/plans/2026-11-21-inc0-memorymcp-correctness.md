# Increment 0: MemoryMcp.Core Correctness Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make memory deletion physically erase vectors and metadata, make tag-filtered search exact, and give every threshold-based caller a dense-only `FindSimilarAsync` before any hybrid search exists.

**Architecture:** The official sqlite-vec v0.1.9 loadable binaries are vendored into `MemoryMcp.Core/runtimes/<rid>/native/` (no NuGet package newer than 0.1.7-alpha.2.1 exists) and loaded by full path through a small `SqliteVecExtension` helper that refuses versions older than 0.1.9. `SqliteVecMemoryStore` turns on `secure_delete`, truncates the WAL after deletes, rebuilds legacy vector tables once (`user_version` 1), and replaces the `limit×10` KNN post-filter with one exact cosine scan whose tag filter runs in the `WHERE` clause. `IMemoryService.FindSimilarAsync` is the pure-cosine API; Cortex consolidation, compaction and subagent context injection, plus the ingest duplicate guard, move onto it.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite 10 + SQLitePCLRaw.bundle_e_sqlcipher 2.1.11, sqlite-vec v0.1.9 (vendored), xUnit v3 + NSubstitute (memory-mcp), xUnit 2 + NSubstitute (Cortex), Docker (agent image).

Spec: `docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md` (Part I §4, row 0).
Defects: `docs/superpowers/research/2026-11-21-memory-search-scale-path.md` ("Current-code defects found (to fix)").

## Background the engineer needs

- **`lib/memory-mcp` is a git submodule** (`https://github.com/yury-opolev/memory-mcp.git`, default branch `main`). It is also used on its own as a stdio MCP server, so it must stay self-contained. Its HEAD is currently **detached** at `c786555` (= `origin/main`). All `lib/memory-mcp` changes are committed **inside the submodule** on branch `feat/inc0-correctness`, pushed to the submodule's `origin`, and the Cortex repo records the new submodule commit. Use `git -C lib\memory-mcp ...` for submodule git commands.
- **Build settings differ.** `lib/memory-mcp` has its own `Directory.Build.props` (no `TreatWarningsAsErrors`) and `lib/Directory.Packages.props` turns central package management **off** under `lib/`, so memory-mcp projects keep inline `Version=` attributes. Cortex projects (`src/`, `tests/`) use central package management and `TreatWarningsAsErrors` — Cortex builds must be warning-free.
- **Test frameworks differ.** memory-mcp tests use **xUnit v3** (`using Xunit;` per file, analyzer xUnit1051 wants `TestContext.Current.CancellationToken`). Cortex tests use xUnit 2 with global usings for `Xunit` and `NSubstitute`.
- **Why vendoring.** `dotnet package search sqlite-vec --exact-match --prerelease` shows only `0.1.7-alpha.2` and `0.1.7-alpha.2.1` on nuget.org. That build only clears a validity bit on `DELETE`; v0.1.7+ (`vec0Update_Delete_ClearVectors`) overwrites the vector slot with zeros. The only newer package (`HiraokaHyperTools.sqlite-vec`) is a third-party repack — not used. GitHub release v0.1.9 publishes `checksums.txt` and GitHub records a sha256 digest per asset; there are no Sigstore attestations, so the trust anchor is "both GitHub-hosted hashes match".
- **How the old package loaded vec0.** The NuGet package put `runtimes/<rid>/native/vec0.*` into `deps.json`, and `LoadExtension("vec0")` found it through the native search directories. Vendored files are plain content, not `deps.json` native assets, so the helper builds the full path `AppContext.BaseDirectory/runtimes/<rid>/native/vec0.<ext>` itself. `None` items with `CopyToOutputDirectory`/`CopyToPublishDirectory` flow transitively: the files appear in `MemoryMcp.Core.Tests`, `Cortex.Contained.Agent.Host.Tests` and the Agent.Host publish output (verified).
- **Docker.** `src/Cortex.Contained.Agent.Host/Dockerfile` copies `lib/memory-mcp/src/MemoryMcp.Core/` and runs `dotnet publish`; `.dockerignore` excludes only `bin/`, `obj/`, `.git/` and similar, so `runtimes/linux-x64/native/vec0.so` ships to `/app/runtimes/linux-x64/native/vec0.so` with **no Dockerfile change**. The agent image is Ubuntu 24.04; the official `vec0.so` v0.1.9 loads there (verified with python3 `load_extension`).
- **Behaviour facts verified while writing this plan** (prototype against the real binaries):
  - old vec0 + delete → marker vector bytes stay in the file; vec0 0.1.9 + delete → gone.
  - vec0 0.1.9 without `secure_delete` → the deleted row's title and memory id stay in free space; with `PRAGMA secure_delete = ON` → gone.
  - in WAL mode the pre-delete page stays in `-wal` until `PRAGMA wal_checkpoint(TRUNCATE)`.
  - vec0 0.1.9 reads, searches and deletes tables created by 0.1.7-alpha.2.1, and 0.1.7-alpha.2.1 reads tables created by 0.1.9 (so an image rollback stays readable). vec0 has no `xRename`, so a table rebuild goes through a TEMP copy.
  - `FROM chunks c CROSS JOIN chunks_vec v WHERE v.ChunkKey = c.MemoryId || ':' || c.ChunkIndex` plans as `SCAN c` + vec0 point lookup (`INDEX 3:2!`), and `vec_distance_cosine` returns exactly the KNN distance.
- **Erasure scope of this increment:** the memory database (`memory.db` + journal/WAL). Content files (`memories/<id>.memory.data`) are deleted with `File.Delete` as before; overwriting files is out of scope (SSD/overlayfs make it unreliable anyway).

## Global Constraints

- Search semantics preserved: score = `1 - cosine distance`, best chunk per memory, `minScore` inclusive (`score >= minScore`), tags match if **any** requested tag equals a chunk tag under `StringComparer.OrdinalIgnoreCase`, results ordered by score descending, top `limit`.
- `FindSimilarAsync` never takes tags and never fuses keyword scores; it is the only API threshold-based callers may use.
- sqlite-vec minimum version is **0.1.9**; anything older throws at `InitializeAsync`.
- memory-mcp style: `this.` for members, braces always, `ILogger` extension calls (no source generators there). Cortex style: `this.` members, `ConfigureAwait(false)`, `[LoggerMessage]` for new logs (none needed here).
- Test names `Method_Condition_Expected`.
- Every commit message ends with the trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
- Run all commands from the Cortex repo root `C:\Users\yurio\Documents\github\cortex-agent` in PowerShell.

## File Structure

memory-mcp submodule (`lib/memory-mcp/`):

| File | Change | Responsibility |
|---|---|---|
| `scripts/update-sqlite-vec.ps1` | create | Download, verify (2 hashes), extract and license the official vec0 binaries; write the version manifest |
| `src/MemoryMcp.Core/runtimes/{win-x64,linux-x64,linux-arm64,osx-x64,osx-arm64}/native/vec0.*` | create (binary) | Vendored sqlite-vec v0.1.9 |
| `src/MemoryMcp.Core/runtimes/SQLITE-VEC-VERSION.txt`, `sqlite-vec-LICENSE-MIT.txt`, `sqlite-vec-LICENSE-APACHE.txt` | create | Provenance and licenses |
| `.gitattributes` | modify | Mark native libraries binary |
| `src/MemoryMcp.Core/MemoryMcp.Core.csproj` | modify | Drop `sqlite-vec` package; copy `runtimes/**/native/*` on build and publish |
| `src/MemoryMcp.Core/Storage/SqliteVecExtension.cs` | create | Resolve, load and version-check vec0 |
| `src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs` | modify | secure_delete, WAL truncate, legacy rebuild, exact filtered scan |
| `src/MemoryMcp.Core/Storage/IMemoryStore.cs` | modify | Document exact-search contract |
| `src/MemoryMcp.Core/Services/IMemoryService.cs`, `MemoryService.cs` | modify | `FindSimilarAsync` (text + vector); duplicate guard uses it |
| `tests/MemoryMcp.Core.Tests/Storage/DatabaseBytes.cs` | create | Read DB/journal/WAL bytes while the DB is open |
| `tests/MemoryMcp.Core.Tests/Storage/SqliteVecExtensionTests.cs` | create | Loader/version tests |
| `tests/MemoryMcp.Core.Tests/Storage/SqliteVecErasureTests.cs` | create | Byte-level erasure gate tests |
| `tests/MemoryMcp.Core.Tests/Storage/ExactSearchTests.cs` | create | Filtered recall = brute force; candidate sets = previous KNN |
| `tests/MemoryMcp.Core.Tests/Services/MemoryServiceTests.cs` | modify | `FindSimilarAsync` unit tests |
| `README.md` | modify | Search pipeline, vendored sqlite-vec, test counts |

Cortex repo:

| File | Change |
|---|---|
| `lib/memory-mcp` (gitlink) | bump to the submodule commit |
| `src/Cortex.Contained.Agent.Host/Memory/MemoryConsolidationService.cs:149-154` | `SearchAsync` → `FindSimilarAsync` |
| `src/Cortex.Contained.Agent.Host/Memory/MemoryCompactionService.cs:232-237` | `SearchAsync` → `FindSimilarAsync` |
| `src/Cortex.Contained.Agent.Host/Agent/SubagentExecutor.cs:152-154, 193-206` | `SearchAsync` → `FindSimilarAsync`, drop unused `tags` parameter |
| `tests/Cortex.Contained.Agent.Host.Tests/MemoryConsolidationServiceTests.cs` | mocks → `FindSimilarAsync`; new routing test |
| `tests/Cortex.Contained.Agent.Host.Tests/MemoryCompactionServiceTests.cs` | mocks → `FindSimilarAsync` |
| `tests/Cortex.Contained.Agent.Host.Tests/SubagentExecutorMemoryContextTests.cs` | create |
| `docs/superpowers/research/2026-11-21-memory-search-scale-path.md` | mark defects fixed |

Not touched: `lib/memory-mcp/tests/SqlCipherCompatTest` (a historical spike, not in `MemoryMcp.slnx`, keeps its own old package reference); `src/Cortex.Contained.Agent.Host/Dockerfile` (no change needed — verified in Task 11).

---

### Task 1: Branches and baseline

**Files:** none (git state only)

- [ ] **Step 1: Create the submodule branch from `origin/main`**

```powershell
git -C lib\memory-mcp fetch origin
git -C lib\memory-mcp status -sb
git -C lib\memory-mcp switch -c feat/inc0-correctness origin/main
```

Expected: `status` shows `## HEAD (no branch)` with no changes; `switch` prints `Switched to a new branch 'feat/inc0-correctness'` and `branch 'feat/inc0-correctness' set up to track 'origin/main'`. Then unset that upstream so a later `git push` goes to the new remote branch:

```powershell
git -C lib\memory-mcp branch --unset-upstream
```

- [ ] **Step 2: Confirm the Cortex branch**

```powershell
git branch --show-current
```

Expected: `feat/episodic-history-memory`. (`version.json` may show as modified locally — leave it alone and never stage it.)

- [ ] **Step 3: Baseline the memory-mcp unit tests**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj
```

Expected: `Passed!  - Failed: 0, Passed: 80, Skipped: 0, Total: 80`.

---

### Task 2: Vendor sqlite-vec v0.1.9 native binaries

**Files:**
- Create: `lib/memory-mcp/scripts/update-sqlite-vec.ps1`
- Create (generated): `lib/memory-mcp/src/MemoryMcp.Core/runtimes/**`
- Modify: `lib/memory-mcp/.gitattributes`

- [ ] **Step 1: Write the vendoring script**

Create `lib/memory-mcp/scripts/update-sqlite-vec.ps1`:

```powershell
<#
.SYNOPSIS
    Vendors the official sqlite-vec loadable extension (vec0) into MemoryMcp.Core.

.DESCRIPTION
    Downloads the loadable-extension archives of one sqlite-vec release from
    https://github.com/asg017/sqlite-vec/releases with the GitHub CLI, verifies every
    archive against the release's checksums.txt AND the digest GitHub recorded for
    the asset, extracts vec0 into src/MemoryMcp.Core/runtimes/<rid>/native/, copies
    the upstream licenses, and writes src/MemoryMcp.Core/runtimes/SQLITE-VEC-VERSION.txt
    (version + archive and file sha256s). Requires `gh` (signed in) and `tar`.

.EXAMPLE
    pwsh scripts/update-sqlite-vec.ps1 -Version 0.1.9
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$runtimesDir = Join-Path $repoRoot 'src/MemoryMcp.Core/runtimes'
$work = Join-Path $repoRoot '.sqlite-vec-download'
$tag = "v$Version"
$upstream = 'asg017/sqlite-vec'

# release asset platform -> .NET runtime identifier and library file name
$targets = [ordered]@{
    'linux-x86_64'   = @{ Rid = 'linux-x64'; File = 'vec0.so' }
    'linux-aarch64'  = @{ Rid = 'linux-arm64'; File = 'vec0.so' }
    'windows-x86_64' = @{ Rid = 'win-x64'; File = 'vec0.dll' }
    'macos-x86_64'   = @{ Rid = 'osx-x64'; File = 'vec0.dylib' }
    'macos-aarch64'  = @{ Rid = 'osx-arm64'; File = 'vec0.dylib' }
}

function Get-Sha256([string]$path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $patterns = @('checksums.txt') + ($targets.Keys | ForEach-Object { "sqlite-vec-$Version-loadable-$_.tar.gz" })
    $ghArgs = @('release', 'download', $tag, '-R', $upstream, '-D', $work)
    foreach ($p in $patterns) { $ghArgs += @('-p', $p) }
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release download failed for $tag" }

    $checksums = @{}
    foreach ($line in Get-Content (Join-Path $work 'checksums.txt')) {
        $parts = $line -split '\s+'
        if ($parts.Count -ge 2) { $checksums[$parts[0]] = $parts[1].ToLowerInvariant() }
    }

    $digestJson = & gh release view $tag -R $upstream --json assets
    if ($LASTEXITCODE -ne 0) { throw "gh release view failed for $tag" }
    $digests = @{}
    foreach ($asset in ($digestJson | ConvertFrom-Json).assets) {
        if ($asset.digest) { $digests[$asset.name] = ($asset.digest -replace '^sha256:', '').ToLowerInvariant() }
    }

    $manifest = [System.Collections.Generic.List[string]]::new()
    $manifest.Add("sqlite-vec $tag (https://github.com/$upstream/releases/tag/$tag)")
    $manifest.Add('Vendored by scripts/update-sqlite-vec.ps1. Do not edit by hand.')

    foreach ($platform in $targets.Keys) {
        $archive = "sqlite-vec-$Version-loadable-$platform.tar.gz"
        $archivePath = Join-Path $work $archive
        $actual = Get-Sha256 $archivePath
        if ($checksums[$archive] -ne $actual) { throw "checksums.txt mismatch for $archive (got $actual)" }
        if ($digests[$archive] -ne $actual) { throw "GitHub asset digest mismatch for $archive (got $actual)" }

        $target = $targets[$platform]
        $destDir = Join-Path $runtimesDir "$($target.Rid)/native"
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        & tar -xzf $archivePath -C $destDir $target.File
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }

        $fileHash = Get-Sha256 (Join-Path $destDir $target.File)
        $manifest.Add("archive $actual  $archive")
        $manifest.Add("file    $fileHash  $($target.Rid)/native/$($target.File)")
    }

    foreach ($license in 'LICENSE-MIT', 'LICENSE-APACHE') {
        $text = & gh api "repos/$upstream/contents/$($license)?ref=$tag" -H 'Accept: application/vnd.github.raw'
        if ($LASTEXITCODE -ne 0) { throw "could not fetch $license at $tag" }
        Set-Content -LiteralPath (Join-Path $runtimesDir "sqlite-vec-$license.txt") -Value $text -Encoding utf8NoBOM
    }

    Set-Content -LiteralPath (Join-Path $runtimesDir 'SQLITE-VEC-VERSION.txt') -Value $manifest -Encoding utf8NoBOM
    Write-Host "Vendored sqlite-vec $tag into $runtimesDir" -ForegroundColor Green
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}
```

- [ ] **Step 2: Run it**

```powershell
pwsh -NoProfile -File lib\memory-mcp\scripts\update-sqlite-vec.ps1 -Version 0.1.9
Get-Content lib\memory-mcp\src\MemoryMcp.Core\runtimes\SQLITE-VEC-VERSION.txt
```

Expected: `Vendored sqlite-vec v0.1.9 into ...` and exactly this manifest (these hashes were verified while writing the plan; a difference means the release changed — stop and investigate):

```text
sqlite-vec v0.1.9 (https://github.com/asg017/sqlite-vec/releases/tag/v0.1.9)
Vendored by scripts/update-sqlite-vec.ps1. Do not edit by hand.
archive b959baa1d8dc88861b1edb337b8587178cdcb12d60b4998f9d10b6a82052d5d7  sqlite-vec-0.1.9-loadable-linux-x86_64.tar.gz
file    5923730861b86c707cca5602b5f91092f9e52a46706dbc6e269fd4bb9c4498e8  linux-x64/native/vec0.so
archive ea03d39541e478fab5974253c461e1cb5d77742f69e40cf96e3fad5bc309a37c  sqlite-vec-0.1.9-loadable-linux-aarch64.tar.gz
file    0b84cbd06418ca3040827deddd650539be05be0f657952426b926c8606217437  linux-arm64/native/vec0.so
archive 51581189d52066b4dfc6631f6d7a3eab7dedc2260656ab09ca97ab3fb8165983  sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz
file    fcf98662a7ad9dce394b96a88f91032047823831b951c76636787c312a6476e6  win-x64/native/vec0.dll
archive 53ad76e400786515e2edcaed2f01271dda846316390b761fadbd2dcf56aa4713  sqlite-vec-0.1.9-loadable-macos-x86_64.tar.gz
file    d39d33d16d302d57440d9a9cdbe3cc095e8324aa96b979b5171f545a6346996d  osx-x64/native/vec0.dylib
archive 8282126333399ddfe98bbbcc7a1936e7252625aac49df056a98be602e46bfd29  sqlite-vec-0.1.9-loadable-macos-aarch64.tar.gz
file    193e480c50b59a55977d166f4aaf0e1bc8832d6963516e5950f39e4d2ce0b793  osx-arm64/native/vec0.dylib
```

- [ ] **Step 3: Mark native libraries binary**

Append to `lib/memory-mcp/.gitattributes`:

```gitattributes

# Vendored sqlite-vec native libraries (scripts/update-sqlite-vec.ps1): never touch line endings.
*.dll   binary
*.so    binary
*.dylib binary
```

- [ ] **Step 4: Check the files are tracked as binary and not ignored**

```powershell
git -C lib\memory-mcp add -A scripts/update-sqlite-vec.ps1 src/MemoryMcp.Core/runtimes .gitattributes
git -C lib\memory-mcp status --short
git -C lib\memory-mcp check-attr diff -- src/MemoryMcp.Core/runtimes/win-x64/native/vec0.dll
```

Expected: `status` lists `A  .gitattributes` (or `M`), `A  scripts/update-sqlite-vec.ps1`, the 5 `vec0.*` files, the manifest and the 2 license files; `check-attr` prints `...vec0.dll: diff: unset`.

- [ ] **Step 5: Commit (submodule)**

```powershell
git -C lib\memory-mcp commit -m "build: vendor sqlite-vec v0.1.9 loadable binaries" -m "NuGet stops at 0.1.7-alpha.2.1, whose DELETE only clears a validity bit. The script verifies each archive against checksums.txt and the GitHub asset digest." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: Load the vendored sqlite-vec ≥ 0.1.9 (gate: deleted vectors leave no bytes)

**Files:**
- Create: `lib/memory-mcp/src/MemoryMcp.Core/Storage/SqliteVecExtension.cs`
- Modify: `lib/memory-mcp/src/MemoryMcp.Core/MemoryMcp.Core.csproj`
- Modify: `lib/memory-mcp/src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs:77-78, 110-111`
- Test: `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/DatabaseBytes.cs` (create)
- Test: `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/SqliteVecErasureTests.cs` (create)
- Test: `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/SqliteVecExtensionTests.cs` (create)

- [ ] **Step 1: Write the byte-scanning helper**

Create `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/DatabaseBytes.cs`:

```csharp
namespace MemoryMcp.Core.Tests.Storage;

/// <summary>
/// Byte-level inspection of a SQLite database and its journal/WAL side files.
/// Files are opened with full sharing so they can be read while the store's
/// connection is still open (Windows would otherwise refuse the read).
/// </summary>
internal static class DatabaseBytes
{
    public static bool Contains(string databasePath, byte[] needle)
    {
        foreach (var path in new[] { databasePath, databasePath + "-journal", databasePath + "-wal" })
        {
            if (File.Exists(path) && ReadShared(path).AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public static byte[] Of(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
```

- [ ] **Step 2: Write the failing gate test**

Create `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/SqliteVecErasureTests.cs`:

```csharp
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Core.Tests.Storage;

/// <summary>
/// Gate tests for physical erasure: after a delete, no byte of the deleted memory may remain
/// in the database file (or its journal/WAL). Each test plants a unique marker and scans the raw file.
/// </summary>
public sealed class SqliteVecErasureTests : IDisposable
{
    private const int Dimensions = 8;

    // Distinctive float32 values: their 32-byte little-endian pattern does not occur by chance.
    private static readonly float[] MarkerVector =
        [1234.5678f, -8765.4321f, 3.14159f, 2.71828f, 1.41421f, -0.57721f, 6.62607f, 299.792f];

    private readonly string tempDir;
    private readonly MemoryMcpOptions options;
    private readonly SqliteVecMemoryStore store;

    public SqliteVecErasureTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memorymcp_erasure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.options = new MemoryMcpOptions
        {
            DataDirectory = this.tempDir,
            DatabaseFileName = "erasure.db",
            Ollama = new OllamaOptions { Dimensions = Dimensions },
        };
        this.store = new SqliteVecMemoryStore(
            Options.Create(this.options), new NullContentEncryptor(), Substitute.For<ILogger<SqliteVecMemoryStore>>());
    }

    public void Dispose()
    {
        this.store.Dispose();
        try
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a pooled handle may still be closing
        }
    }

    private static List<ChunkRecord> SingleChunk(string memoryId, string title) =>
    [
        new()
        {
            MemoryId = memoryId, ChunkIndex = 0, StartOffset = 0, Length = 10,
            Title = title, Tags = ["erasure"],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        },
    ];

    private static float[] Filler(int seed)
    {
        var rng = new Random(seed);
        var vector = new float[Dimensions];
        for (int i = 0; i < Dimensions; i++)
        {
            vector[i] = (float)rng.NextDouble();
        }

        return vector;
    }

    [Fact]
    public async Task DeleteMemoryAsync_VectorBytesAreErasedFromDatabaseFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.store.InitializeAsync(ct);

        // Neighbours share the victim's vector chunk, so only an in-place wipe can remove it.
        for (int i = 0; i < 3; i++)
        {
            await this.store.StoreMemoryAsync($"keep-{i}", "keep", SingleChunk($"keep-{i}", "keep"), [Filler(i)], ct);
        }

        await this.store.StoreMemoryAsync("victim", "victim", SingleChunk("victim", "victim"), [MarkerVector], ct);
        var marker = DatabaseBytes.Of(MarkerVector);
        Assert.True(DatabaseBytes.Contains(this.options.DatabasePath, marker), "sanity: marker must be visible before the delete");

        Assert.True(await this.store.DeleteMemoryAsync("victim", ct));

        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, marker), "deleted vector bytes are still in the database file");
    }
}
```

- [ ] **Step 3: Run it to verify it fails on the old package**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj --filter "FullyQualifiedName~SqliteVecErasureTests"
```

Expected: FAIL — `deleted vector bytes are still in the database file` (vec0 0.1.7-alpha.2.1 only clears the validity bit).

- [ ] **Step 4: Write the loader tests**

Create `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/SqliteVecExtensionTests.cs`:

```csharp
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Core.Tests.Storage;

public class SqliteVecExtensionTests
{
    [Fact]
    public void Load_VendoredLibrary_IsAtLeastMinimumVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var version = SqliteVecExtension.Load(connection);

        var parsed = Version.Parse(version.TrimStart('v').Split('-', 2)[0]);
        Assert.True(parsed >= SqliteVecExtension.MinimumVersion, $"loaded sqlite-vec {version}");
    }

    [Theory]
    [InlineData("v0.1.7-alpha.2.1")]
    [InlineData("v0.1.8")]
    [InlineData("garbage")]
    public void EnsureSupported_OlderOrUnparsableVersion_Throws(string version)
    {
        Assert.Throws<InvalidOperationException>(() => SqliteVecExtension.EnsureSupported(version));
    }

    [Theory]
    [InlineData("v0.1.9")]
    [InlineData("v0.1.10-alpha.4")]
    [InlineData("v0.2.0")]
    public void EnsureSupported_SupportedVersion_DoesNotThrow(string version)
    {
        SqliteVecExtension.EnsureSupported(version);
    }
}
```

- [ ] **Step 5: Run to verify it fails**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj --filter "FullyQualifiedName~SqliteVecExtensionTests"
```

Expected: build FAILS — `error CS0103: The name 'SqliteVecExtension' does not exist in the current context`.

- [ ] **Step 6: Implement the loader**

Create `lib/memory-mcp/src/MemoryMcp.Core/Storage/SqliteVecExtension.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// Loads the sqlite-vec extension (vec0) vendored under <c>runtimes/&lt;rid&gt;/native/</c>
/// (see <c>scripts/update-sqlite-vec.ps1</c>) and rejects releases older than
/// <see cref="MinimumVersion"/>, the first one whose DELETE wipes the vector bytes.
/// </summary>
internal static class SqliteVecExtension
{
    /// <summary>Oldest sqlite-vec release that physically clears deleted vectors.</summary>
    public static readonly Version MinimumVersion = new(0, 1, 9);

    /// <summary>
    /// Loads vec0 into <paramref name="connection"/> and returns the loaded version (e.g. "v0.1.9").
    /// </summary>
    public static string Load(SqliteConnection connection)
    {
        connection.LoadExtension(ResolvePath(AppContext.BaseDirectory));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT vec_version()";
        var version = (string)cmd.ExecuteScalar()!;
        EnsureSupported(version);
        return version;
    }

    /// <summary>Full path of the vendored vec0 library for the current OS and CPU.</summary>
    public static string ResolvePath(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "runtimes", GetRuntimeIdentifier(), "native", GetFileName());
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"sqlite-vec native library not found at '{path}'. It is vendored in MemoryMcp.Core/runtimes and copied on build.",
                path);
        }

        return path;
    }

    /// <summary>Throws when <paramref name="vecVersion"/> (as returned by <c>vec_version()</c>) is too old.</summary>
    public static void EnsureSupported(string vecVersion)
    {
        var core = vecVersion.TrimStart('v').Split('-', 2)[0];
        if (!Version.TryParse(core, out var parsed) || parsed < MinimumVersion)
        {
            throw new InvalidOperationException(
                $"sqlite-vec {vecVersion} is not supported: {MinimumVersion} or newer is required so that deletes erase vector bytes.");
        }
    }

    private static string GetRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : throw new PlatformNotSupportedException("sqlite-vec is vendored for Windows, Linux and macOS only.");
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => throw new PlatformNotSupportedException($"sqlite-vec is not vendored for {other}."),
        };
        return $"{os}-{arch}";
    }

    private static string GetFileName() =>
        OperatingSystem.IsWindows() ? "vec0.dll" : OperatingSystem.IsMacOS() ? "vec0.dylib" : "vec0.so";
}
```

(`win-arm64` has no official build; it fails with the clear `FileNotFoundException` above, as it did before with the NuGet package.)

- [ ] **Step 7: Swap the package for the vendored files**

In `lib/memory-mcp/src/MemoryMcp.Core/MemoryMcp.Core.csproj` delete this line:

```xml
    <PackageReference Include="sqlite-vec" Version="0.1.7-alpha.2.1" />
```

and add, just before `</Project>`:

```xml
  <!-- sqlite-vec (vec0) is vendored from the official GitHub release by scripts/update-sqlite-vec.ps1:
       the NuGet package stopped at 0.1.7-alpha.2.1, which predates real vector deletion.
       SqliteVecExtension loads runtimes/<rid>/native/vec0.* from the output directory. -->
  <ItemGroup>
    <None Update="runtimes\**\native\*" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>

```

- [ ] **Step 8: Load through the helper in the store**

In `lib/memory-mcp/src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs` (`InitializeAsync`) replace:

```csharp
        // Load sqlite-vec extension
        this.connection.LoadExtension("vec0");
```

with:

```csharp
        var vecVersion = SqliteVecExtension.Load(this.connection);
```

and replace the final log call of `InitializeAsync`:

```csharp
        this.logger.LogInformation("Memory store initialized. Database: {DatabasePath}, Dimensions: {Dimensions}",
            this.options.DatabasePath, this.options.Ollama.Dimensions);
```

with:

```csharp
        this.logger.LogInformation("Memory store initialized. Database: {DatabasePath}, Dimensions: {Dimensions}, sqlite-vec: {VecVersion}",
            this.options.DatabasePath, this.options.Ollama.Dimensions, vecVersion);
```

- [ ] **Step 9: Run the whole unit-test project**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj
Get-ChildItem lib\memory-mcp\tests\MemoryMcp.Core.Tests\bin\Debug\net10.0\runtimes -Recurse -Filter "vec0*" | ForEach-Object FullName
```

Expected: `Passed!  - Failed: 0, Passed: 88, Skipped: 0, Total: 88`, and 5 `vec0.*` files listed under `runtimes\<rid>\native\`.

- [ ] **Step 10: Commit (submodule)**

```powershell
git -C lib\memory-mcp add -A src/MemoryMcp.Core tests/MemoryMcp.Core.Tests
git -C lib\memory-mcp commit -m "fix: load vendored sqlite-vec >= 0.1.9 so DELETE erases vector bytes" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: `secure_delete` and WAL truncation after deletes

**Files:**
- Modify: `lib/memory-mcp/src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs` (`InitializeAsync`, `DeleteMemoryAsync`)
- Test: `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/SqliteVecErasureTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `SqliteVecErasureTests` (after `DeleteMemoryAsync_VectorBytesAreErasedFromDatabaseFile`):

```csharp
    [Fact]
    public async Task DeleteMemoryAsync_MetadataBytesAreErasedFromDatabaseFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.store.InitializeAsync(ct);
        await this.store.StoreMemoryAsync("keep", "keep", SingleChunk("keep", "keep"), [Filler(1)], ct);

        var victimId = $"victim-{Guid.NewGuid():N}";
        var title = $"TITLE-MARKER-{Guid.NewGuid():N}";
        await this.store.StoreMemoryAsync(victimId, "victim", SingleChunk(victimId, title), [Filler(2)], ct);
        var titleBytes = System.Text.Encoding.UTF8.GetBytes(title);
        var idBytes = System.Text.Encoding.UTF8.GetBytes(victimId);
        Assert.True(DatabaseBytes.Contains(this.options.DatabasePath, titleBytes), "sanity: title must be visible before the delete");

        Assert.True(await this.store.DeleteMemoryAsync(victimId, ct));

        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, titleBytes), "deleted title bytes are still in the database file");
        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, idBytes), "deleted memory id bytes are still in the database file");
    }

    [Fact]
    public async Task DeleteMemoryAsync_WalMode_LeavesNoCopyInWal()
    {
        var ct = TestContext.Current.CancellationToken;
        var walOptions = new MemoryMcpOptions
        {
            DataDirectory = this.tempDir,
            DatabaseFileName = "erasure-wal.db",
            Ollama = new OllamaOptions { Dimensions = Dimensions },
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={walOptions.DatabasePath}");
        connection.Open();
        using (var walCmd = connection.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode = WAL;";
            walCmd.ExecuteNonQuery();
        }

        using var walStore = new SqliteVecMemoryStore(
            connection, walOptions, new NullContentEncryptor(), Substitute.For<ILogger<SqliteVecMemoryStore>>());
        await walStore.InitializeAsync(ct);
        await walStore.StoreMemoryAsync("keep", "keep", SingleChunk("keep", "keep"), [Filler(1)], ct);
        await walStore.StoreMemoryAsync("victim", "victim", SingleChunk("victim", "victim"), [MarkerVector], ct);
        var marker = DatabaseBytes.Of(MarkerVector);
        Assert.True(DatabaseBytes.Contains(walOptions.DatabasePath, marker), "sanity: marker must be visible before the delete");

        Assert.True(await walStore.DeleteMemoryAsync("victim", ct));

        Assert.False(DatabaseBytes.Contains(walOptions.DatabasePath, marker), "deleted vector bytes survive in the database or its WAL");
    }
```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj --filter "FullyQualifiedName~SqliteVecErasureTests"
```

Expected: 2 FAIL — `deleted title bytes are still in the database file` and `deleted vector bytes survive in the database or its WAL`; the vector test from Task 3 passes.

- [ ] **Step 3: Implement**

In `SqliteVecMemoryStore.InitializeAsync`, directly after `var vecVersion = SqliteVecExtension.Load(this.connection);` insert:

```csharp

        // Overwrite deleted content with zeros so a deleted memory leaves no bytes in the file.
        // The pragma is per connection, so it is set on every open.
        using (var secureDeleteCmd = this.connection.CreateCommand())
        {
            secureDeleteCmd.CommandText = "PRAGMA secure_delete = ON;";
            await secureDeleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }
```

In `SqliteVecMemoryStore.DeleteMemoryAsync`, between the `try { ... transaction.Commit(); } catch { ... }` block and `// Delete content file`, insert:

```csharp
        // No-op in the default rollback-journal mode; in WAL mode it moves the wiped pages
        // into the database file and truncates the WAL so no pre-delete copy survives there.
        using (var checkpointCmd = this.connection.CreateCommand())
        {
            checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpointCmd.ExecuteNonQueryAsync(cancellationToken);
        }

```

- [ ] **Step 4: Run to verify they pass**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj
```

Expected: `Passed!  - Failed: 0, Passed: 90, Skipped: 0, Total: 90`.

- [ ] **Step 5: Commit (submodule)**

```powershell
git -C lib\memory-mcp add -A src/MemoryMcp.Core tests/MemoryMcp.Core.Tests
git -C lib\memory-mcp commit -m "fix: secure_delete and WAL truncate so deleted memories leave no bytes" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: One-time rebuild of legacy vector tables

Databases written by vec0 0.1.7-alpha.2.1 still hold the vectors of every memory deleted so far (nightly compaction deletes duplicates). Rebuilding the table once with 0.1.9 and vacuuming removes them. `PRAGMA user_version` (currently unused, 0) records that the rebuild is done.

**Files:**
- Modify: `lib/memory-mcp/src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs` (`InitializeAsync`; new `CreateVectorTableSql`, `RebuildLegacyVectorTableAsync`)
- Test: `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/SqliteVecErasureTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `SqliteVecErasureTests`:

```csharp
    [Fact]
    public async Task InitializeAsync_LegacyDatabase_RebuildsVectorTableWithoutUnreferencedVectors()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = new float[] { 1, 0, 0, 0, 0, 0, 0, 0 };
        CreateLegacyDatabase(this.options.DatabasePath, live);
        var marker = DatabaseBytes.Of(MarkerVector);
        Assert.True(DatabaseBytes.Contains(this.options.DatabasePath, marker), "sanity: legacy residue must be visible before the rebuild");

        await this.store.InitializeAsync(ct);

        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, marker), "legacy vector residue survived the rebuild");
        Assert.Equal(1L, ReadUserVersion(this.options.DatabasePath));
        var results = await this.store.SearchAsync(live, limit: 5, cancellationToken: ct);
        Assert.Equal("live", Assert.Single(results).MemoryId);
    }

    /// <summary>
    /// Builds a schema-version-0 database: one live memory plus one vector with no chunk row.
    /// The unreferenced vector stands in for residue left behind by sqlite-vec &lt; 0.1.9 deletes.
    /// </summary>
    private static void CreateLegacyDatabase(string databasePath, float[] liveVector)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        SqliteVecExtension.Load(connection);

        void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            cmd.ExecuteNonQuery();
        }

        Execute("""
            CREATE TABLE chunks (
                MemoryId TEXT NOT NULL, ChunkIndex INTEGER NOT NULL, StartOffset INTEGER NOT NULL, Length INTEGER NOT NULL,
                Title TEXT, Tags TEXT DEFAULT '[]', CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (MemoryId, ChunkIndex));
            """);
        Execute($"CREATE VIRTUAL TABLE chunks_vec USING vec0(ChunkKey TEXT PRIMARY KEY, Vector float[{Dimensions}] distance_metric=cosine);");
        var now = DateTimeOffset.UtcNow.ToString("o");
        Execute(
            "INSERT INTO chunks VALUES ('live', 0, 0, 4, 'live', '[]', @now, @now);",
            ("@now", now));
        Execute("INSERT INTO chunks_vec (ChunkKey, Vector) VALUES ('live:0', @v);", ("@v", DatabaseBytes.Of(liveVector)));
        Execute("INSERT INTO chunks_vec (ChunkKey, Vector) VALUES ('ghost:0', @v);", ("@v", DatabaseBytes.Of(MarkerVector)));
    }

    private static long ReadUserVersion(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return (long)cmd.ExecuteScalar()!;
    }
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj --filter "FullyQualifiedName~InitializeAsync_LegacyDatabase"
```

Expected: FAIL — `legacy vector residue survived the rebuild`.

- [ ] **Step 3: Implement**

In `SqliteVecMemoryStore.InitializeAsync` replace the vector-table block:

```csharp
        // Create sqlite-vec virtual table for vectors
        using var createVecCmd = this.connection.CreateCommand();
        createVecCmd.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_vec USING vec0(
                ChunkKey TEXT PRIMARY KEY,
                Vector   float[{this.options.Ollama.Dimensions}] distance_metric=cosine
            );
            """;
        await createVecCmd.ExecuteNonQueryAsync(cancellationToken);
```

with:

```csharp
        // Create sqlite-vec virtual table for vectors
        using var createVecCmd = this.connection.CreateCommand();
        createVecCmd.CommandText = this.CreateVectorTableSql;
        await createVecCmd.ExecuteNonQueryAsync(cancellationToken);

        await this.RebuildLegacyVectorTableAsync(cancellationToken);
```

and add these two members right after `InitializeAsync`:

```csharp
    private string CreateVectorTableSql => $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_vec USING vec0(
            ChunkKey TEXT PRIMARY KEY,
            Vector   float[{this.options.Ollama.Dimensions}] distance_metric=cosine
        );
        """;

    /// <summary>
    /// Schema version 1 means the vector table was rebuilt by sqlite-vec &gt;= 0.1.9. Older releases
    /// only cleared a validity bit on DELETE, so the vectors of deleted memories may still sit in
    /// the old table. Copying the live vectors into a fresh table and vacuuming removes them.
    /// Vectors without a chunk row are unreachable by search and are dropped too.
    /// </summary>
    private async Task RebuildLegacyVectorTableAsync(CancellationToken cancellationToken)
    {
        const int VectorsRebuiltVersion = 1;

        using (var versionCmd = this.connection.CreateCommand())
        {
            versionCmd.CommandText = "PRAGMA user_version;";
            var userVersion = (long)(await versionCmd.ExecuteScalarAsync(cancellationToken))!;
            if (userVersion >= VectorsRebuiltVersion)
            {
                return;
            }
        }

        using (var transaction = this.connection.BeginTransaction())
        {
            string[] steps =
            [
                """
                CREATE TEMP TABLE chunks_vec_rebuild AS
                SELECT v.ChunkKey, v.Vector
                FROM chunks c
                CROSS JOIN chunks_vec v
                WHERE v.ChunkKey = c.MemoryId || ':' || c.ChunkIndex;
                """,
                "DROP TABLE chunks_vec;",
                this.CreateVectorTableSql,
                "INSERT INTO chunks_vec (ChunkKey, Vector) SELECT ChunkKey, Vector FROM chunks_vec_rebuild;",
                "DROP TABLE chunks_vec_rebuild;",
                $"PRAGMA user_version = {VectorsRebuiltVersion};",
            ];

            foreach (var sql in steps)
            {
                using var cmd = this.connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
        }

        using (var vacuumCmd = this.connection.CreateCommand())
        {
            vacuumCmd.CommandText = "VACUUM;";
            await vacuumCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        this.logger.LogInformation("Rebuilt the vector table with sqlite-vec >= 0.1.9 and vacuumed {DatabasePath}.", this.options.DatabasePath);
    }
```

(A fresh database also starts at `user_version` 0; the rebuild of an empty table is instant and stamps it 1.)

- [ ] **Step 4: Run to verify it passes**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj
```

Expected: `Passed!  - Failed: 0, Passed: 91, Skipped: 0, Total: 91`.

- [ ] **Step 5: Commit (submodule)**

```powershell
git -C lib\memory-mcp add -A src/MemoryMcp.Core tests/MemoryMcp.Core.Tests
git -C lib\memory-mcp commit -m "fix: rebuild legacy vector tables once to purge pre-0.1.9 delete residue" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Dense-only `FindSimilarAsync` and the duplicate guard

**Files:**
- Modify: `lib/memory-mcp/src/MemoryMcp.Core/Services/IMemoryService.cs` (after `SearchAsync`)
- Modify: `lib/memory-mcp/src/MemoryMcp.Core/Services/MemoryService.cs:50-56` and end of class
- Test: `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Services/MemoryServiceTests.cs`

**Interfaces:**
- Produces: `IMemoryService.FindSimilarAsync(string text, int limit, float minScore, CancellationToken)` and `IMemoryService.FindSimilarAsync(float[] vector, int limit, float minScore, CancellationToken)`, both `Task<List<SearchResult>>`.

- [ ] **Step 1: Write the failing tests**

In `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Services/MemoryServiceTests.cs`, insert directly above the line `    // --- New duplicate guard tests ---`:

```csharp
    [Fact]
    public async Task FindSimilarAsync_Text_EmbedsTextAndRunsUnfilteredVectorSearch()
    {
        var vector = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        var expected = new List<SearchResult> { new() { MemoryId = "m-1", Content = "c", Score = 0.8f } };
        this.embedding.EmbedAsync("user likes cats", Arg.Any<CancellationToken>()).Returns(vector);
        this.store.SearchAsync(vector, 5, 0.3f, null, Arg.Any<CancellationToken>()).Returns(expected);

        var results = await this.service.FindSimilarAsync("user likes cats", 5, 0.3f, TestContext.Current.CancellationToken);

        Assert.Same(expected, results);
        await this.store.Received(1).SearchAsync(vector, 5, 0.3f, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindSimilarAsync_EmptyText_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => this.service.FindSimilarAsync("  ", 5, 0.3f, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindSimilarAsync_NonPositiveLimit_ThrowsBeforeEmbedding()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => this.service.FindSimilarAsync("text", 0, 0.3f, TestContext.Current.CancellationToken));

        await this.embedding.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

```

- [ ] **Step 2: Run to verify they fail**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj --filter "FullyQualifiedName~MemoryServiceTests.FindSimilarAsync"
```

Expected: build FAILS — `error CS1061: 'MemoryService' does not contain a definition for 'FindSimilarAsync'`.

- [ ] **Step 3: Add the interface members**

In `lib/memory-mcp/src/MemoryMcp.Core/Services/IMemoryService.cs`, after the `SearchAsync` declaration, add:

```csharp

    /// <summary>
    /// Dense-only similarity lookup for threshold-based callers (duplicate guard, consolidation,
    /// compaction, context injection). Embeds <paramref name="text"/> and returns up to
    /// <paramref name="limit"/> memories whose best-chunk cosine similarity is at least
    /// <paramref name="minScore"/>, best first, across all tags.
    /// Unlike <see cref="SearchAsync"/>, which may later fuse keyword ranking, this method always
    /// ranks by pure cosine similarity, so a threshold keeps its meaning.
    /// </summary>
    /// <param name="text">The text to compare against stored memories.</param>
    /// <param name="limit">Maximum number of memories to return (must be positive).</param>
    /// <param name="minScore">Minimum cosine similarity (-1.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Similar memories ordered by descending cosine similarity.</returns>
    Task<List<SearchResult>> FindSimilarAsync(string text, int limit, float minScore, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="FindSimilarAsync(string, int, float, CancellationToken)"/> for an
    /// already-computed embedding <paramref name="vector"/>.
    /// </summary>
    /// <param name="vector">The query embedding.</param>
    /// <param name="limit">Maximum number of memories to return (must be positive).</param>
    /// <param name="minScore">Minimum cosine similarity (-1.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Similar memories ordered by descending cosine similarity.</returns>
    Task<List<SearchResult>> FindSimilarAsync(float[] vector, int limit, float minScore, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Implement and route the duplicate guard**

In `lib/memory-mcp/src/MemoryMcp.Core/Services/MemoryService.cs` (`IngestAsync`) replace:

```csharp
            contentVector = await this.embedding.EmbedAsync(content, cancellationToken);
            var similar = await this.store.SearchAsync(
                contentVector,
                limit: currentOptions.DuplicateSearchLimit,
                minScore: currentOptions.DuplicateThreshold,
                tags: null, // search across all tags
                cancellationToken);
```

with:

```csharp
            contentVector = await this.embedding.EmbedAsync(content, cancellationToken);
            var similar = await this.FindSimilarAsync(
                contentVector,
                currentOptions.DuplicateSearchLimit,
                currentOptions.DuplicateThreshold,
                cancellationToken);
```

and add at the end of the class (after `SearchAsync`):

```csharp

    public async Task<List<SearchResult>> FindSimilarAsync(string text, int limit, float minScore, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text cannot be empty.", nameof(text));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var vector = await this.embedding.EmbedAsync(text, cancellationToken);
        return await this.FindSimilarAsync(vector, limit, minScore, cancellationToken);
    }

    public Task<List<SearchResult>> FindSimilarAsync(float[] vector, int limit, float minScore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // The store's vector search is an exact cosine scan; no tag filter, no keyword fusion.
        return this.store.SearchAsync(vector, limit, minScore, tags: null, cancellationToken);
    }
```

- [ ] **Step 5: Run to verify all pass (the existing duplicate-guard tests still mock `store.SearchAsync` and must stay green)**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj
dotnet test lib\memory-mcp\tests\MemoryMcp.Tests\MemoryMcp.Tests.csproj
```

Expected: `Passed: 94` (Core.Tests) and `Passed: 26` (MemoryMcp.Tests), 0 failed.

- [ ] **Step 6: Commit (submodule)**

```powershell
git -C lib\memory-mcp add -A src/MemoryMcp.Core tests/MemoryMcp.Core.Tests
git -C lib\memory-mcp commit -m "feat: dense-only IMemoryService.FindSimilarAsync; duplicate guard uses it" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Exact search with the tag filter inside the scan

`SearchAsync` fetched the `limit×10` nearest chunks and only then filtered by tags, so a 1%-selective filter finds about half a match. Replace it with one exact scan: chunks are filtered in SQL first, then scored with `vec_distance_cosine` through a vec0 point lookup (no `k` limit, so nothing is truncated). Corpus today is ~350 chunks; exact scan stays well within budget (see the scale-path record).

sqlite-vec metadata/partition columns are **not** used: tags are a multi-valued JSON array and vec0 metadata columns are scalar. SQLite's `NOCASE` only folds ASCII, so the tag comparison is a registered C# function using `StringComparer.OrdinalIgnoreCase` — identical to the old in-process check (tags are often Cyrillic or Danish).

**Files:**
- Modify: `lib/memory-mcp/src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs` (`SearchAsync`, `InitializeAsync`, helpers)
- Modify: `lib/memory-mcp/src/MemoryMcp.Core/Storage/IMemoryStore.cs` (`SearchAsync` doc)
- Test: `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/ExactSearchTests.cs` (create)

- [ ] **Step 1: Write the tests**

Create `lib/memory-mcp/tests/MemoryMcp.Core.Tests/Storage/ExactSearchTests.cs`:

```csharp
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Core.Tests.Storage;

/// <summary>
/// Search correctness against references computed outside the engine:
/// filtered search must equal brute force over the filtered set, and the
/// threshold lookups used by Cortex must return the same candidates as the
/// pre-increment-0 KNN search (fetch limit*10 nearest chunks, then group).
/// </summary>
public sealed class ExactSearchTests : IDisposable
{
    private const int Dimensions = 16;
    private const int Clusters = 8;

    private readonly string tempDir;
    private readonly MemoryMcpOptions options;
    private readonly SqliteVecMemoryStore store;
    private readonly Dictionary<string, (List<float[]> Vectors, List<string> Tags)> seeded = [];

    public ExactSearchTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memorymcp_exact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.options = new MemoryMcpOptions
        {
            DataDirectory = this.tempDir,
            DatabaseFileName = "exact.db",
            Ollama = new OllamaOptions { Dimensions = Dimensions },
        };
        this.store = new SqliteVecMemoryStore(
            Options.Create(this.options), new NullContentEncryptor(), Substitute.For<ILogger<SqliteVecMemoryStore>>());
    }

    public void Dispose()
    {
        this.store.Dispose();
        try
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a pooled handle may still be closing
        }
    }

    [Fact]
    public async Task SearchAsync_SelectiveTagFilter_EqualsBruteForceOverFilteredSet()
    {
        var ct = TestContext.Current.CancellationToken;
        // 1% of 1,000 memories carry the tag: the old limit*10 post-filter found ~0.5 of them.
        await this.SeedAsync(memoryCount: 1000, tagsFor: i => i % 100 == 7 ? ["rare"] : ["common"], ct);
        var query = ClusteredVector(new Random(99), cluster: 3);

        var actual = await this.store.SearchAsync(query, limit: 5, minScore: null, tags: ["rare"], ct);

        var expected = this.BruteForce(query, limit: 5, minScore: null, tags: ["rare"]);
        Assert.Equal(5, expected.Count);
        AssertSameRanking(expected, actual);
    }

    [Fact]
    public async Task SearchAsync_TagFilter_IsCaseInsensitiveForNonAsciiTags()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.SeedAsync(memoryCount: 50, tagsFor: i => i == 10 ? ["Проект"] : ["прочее"], ct);

        var results = await this.store.SearchAsync(ClusteredVector(new Random(1), cluster: 0), limit: 5, tags: ["проект"], cancellationToken: ct);

        Assert.Equal("mem-0010", Assert.Single(results).MemoryId);
    }

    // Parameter sets of the Cortex threshold callers: consolidation (5, 0.3), compaction (10, 0.7),
    // subagent context (3, 0.3) and the ingest duplicate guard (5, 0.9).
    [Theory]
    [InlineData(5, 0.3f)]
    [InlineData(10, 0.7f)]
    [InlineData(3, 0.3f)]
    [InlineData(5, 0.9f)]
    public async Task FindSimilarAsync_CandidateSets_MatchPreviousKnnSearch(int limit, float minScore)
    {
        var ct = TestContext.Current.CancellationToken;
        await this.SeedAsync(memoryCount: 300, tagsFor: _ => [], ct);
        var service = new MemoryService(
            Substitute.For<IChunkingService>(),
            Substitute.For<IEmbeddingService>(),
            this.store,
            Substitute.For<IOptionsMonitor<MemoryMcpOptions>>(),
            Substitute.For<ILogger<MemoryService>>());
        var rng = new Random(2024);
        var nonEmptyQueries = 0;

        for (int q = 0; q < 20; q++)
        {
            var query = ClusteredVector(rng, cluster: q % Clusters);

            var actual = await service.FindSimilarAsync(query, limit, minScore, ct);

            var expected = await this.LegacyKnnSearchAsync(query, limit, minScore, ct);
            AssertSameRanking(expected, actual);
            nonEmptyQueries += expected.Count > 0 ? 1 : 0;
        }

        Assert.True(nonEmptyQueries > 0, "fixture must produce candidates at this threshold");
    }

    private async Task SeedAsync(int memoryCount, Func<int, List<string>> tagsFor, CancellationToken ct)
    {
        await this.store.InitializeAsync(ct);
        var rng = new Random(42);
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < memoryCount; i++)
        {
            var memoryId = $"mem-{i:D4}";
            var tags = tagsFor(i);
            var chunkCount = 1 + (i % 3);
            var chunks = new List<ChunkRecord>();
            var vectors = new List<float[]>();
            for (int c = 0; c < chunkCount; c++)
            {
                chunks.Add(new ChunkRecord
                {
                    MemoryId = memoryId, ChunkIndex = c, StartOffset = c * 10, Length = 10,
                    Title = memoryId, Tags = tags, CreatedAt = now, UpdatedAt = now,
                });
                vectors.Add(ClusteredVector(rng, cluster: i % Clusters));
            }

            await this.store.StoreMemoryAsync(memoryId, $"content {i}", chunks, vectors, ct);
            this.seeded[memoryId] = (vectors, tags);
        }
    }

    /// <summary>A fixed per-cluster centroid plus noise, so in-cluster cosine spans roughly 0.6-0.95.</summary>
    private static float[] ClusteredVector(Random rng, int cluster)
    {
        var centroidRng = new Random(1000 + cluster);
        var vector = new float[Dimensions];
        for (int d = 0; d < Dimensions; d++)
        {
            var centroid = (centroidRng.NextDouble() * 2) - 1;
            var noise = ((rng.NextDouble() * 2) - 1) * 0.5;
            vector[d] = (float)(centroid + noise);
        }

        return vector;
    }

    private List<(string MemoryId, float Score)> BruteForce(float[] query, int limit, float? minScore, List<string>? tags)
    {
        return this.seeded
            .Where(m => tags is null || tags.Any(t => m.Value.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            .Select(m => (MemoryId: m.Key, Score: m.Value.Vectors.Max(v => Cosine(query, v))))
            .Where(r => minScore is null || r.Score >= minScore.Value)
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }

    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

    /// <summary>The search algorithm shipped before increment 0, verbatim, as the reference.</summary>
    private async Task<List<(string MemoryId, float Score)>> LegacyKnnSearchAsync(float[] query, int limit, float minScore, CancellationToken ct)
    {
        using var connection = new SqliteConnection($"Data Source={this.options.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(ct);
        SqliteVecExtension.Load(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT v.ChunkKey, v.distance
            FROM chunks_vec v
            INNER JOIN chunks c ON c.MemoryId || ':' || c.ChunkIndex = v.ChunkKey
            WHERE v.Vector MATCH @queryVector
            AND k = @fetchLimit
            ORDER BY v.distance
            """;
        cmd.Parameters.AddWithValue("@queryVector", DatabaseBytes.Of(query));
        cmd.Parameters.AddWithValue("@fetchLimit", limit * 10);

        var best = new Dictionary<string, float>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var chunkKey = reader.GetString(0);
            var memoryId = chunkKey[..chunkKey.LastIndexOf(':')];
            var score = 1.0f - reader.GetFloat(1);
            if (score < minScore)
            {
                continue;
            }

            if (!best.TryGetValue(memoryId, out var existing) || score > existing)
            {
                best[memoryId] = score;
            }
        }

        return best
            .Select(kv => (MemoryId: kv.Key, Score: kv.Value))
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }

    private static void AssertSameRanking(List<(string MemoryId, float Score)> expected, List<SearchResult> actual)
    {
        Assert.Equal(expected.Select(e => e.MemoryId), actual.Select(a => a.MemoryId));
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Score, actual[i].Score, 4);
        }
    }
}
```

- [ ] **Step 2: Run them on the current (post-filter) implementation**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj --filter "FullyQualifiedName~ExactSearchTests"
```

Expected: 1 FAIL — `SearchAsync_SelectiveTagFilter_EqualsBruteForceOverFilteredSet` with `Assert.Equal() Failure: Collections differ` (expected `["mem-0107", "mem-0507", "mem-0307", "mem-0707", "mem-0907"]`). The 4 `FindSimilarAsync_CandidateSets_MatchPreviousKnnSearch` cases and the non-ASCII tag test PASS: they characterise current behaviour and must still pass after the rewrite.

- [ ] **Step 3: Register the tag-match function**

In `SqliteVecMemoryStore`, add below `private bool disposed;`:

```csharp

    private const string TagsMatchFunction = "memory_tags_match";
```

In `InitializeAsync`, directly before `// Create chunks metadata table`, add:

```csharp
        this.connection.CreateFunction<string?, string, bool>(TagsMatchFunction, TagsMatch, isDeterministic: true);

```

Replace the now-unused helper:

```csharp
    private static (string MemoryId, int ChunkIndex) ParseChunkKey(string chunkKey)
    {
        var lastColon = chunkKey.LastIndexOf(':');
        return (chunkKey[..lastColon], int.Parse(chunkKey[(lastColon + 1)..]));
    }
```

with:

```csharp
    /// <summary>
    /// SQL function <c>memory_tags_match(chunkTagsJson, filterJson)</c>: true when the chunk shares at
    /// least one tag with the filter. Case-insensitive like <see cref="StringComparer.OrdinalIgnoreCase"/>,
    /// which SQLite's ASCII-only NOCASE cannot provide for non-Latin tags.
    /// </summary>
    private static bool TagsMatch(string? chunkTagsJson, string filterJson)
    {
        if (string.IsNullOrEmpty(chunkTagsJson))
        {
            return false;
        }

        var chunkTags = JsonSerializer.Deserialize<List<string>>(chunkTagsJson) ?? [];
        var filter = JsonSerializer.Deserialize<List<string>>(filterJson) ?? [];
        return filter.Any(t => chunkTags.Contains(t, StringComparer.OrdinalIgnoreCase));
    }
```

- [ ] **Step 4: Replace the scan in `SearchAsync`**

In `SqliteVecMemoryStore.SearchAsync`, replace everything from `// Query sqlite-vec for nearest neighbors` up to (not including) `// Sort by score descending, take top limit` with:

```csharp
        // Exact (brute-force) cosine scan over every chunk. Filters sit in the WHERE clause, so
        // only matching chunks are scored and a selective filter cannot starve the result set.
        var hasTagFilter = tags is { Count: > 0 };
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.MemoryId, vec_distance_cosine(v.Vector, @queryVector) AS distance,
                   c.Title, c.Tags, c.CreatedAt, c.UpdatedAt
            FROM chunks c
            CROSS JOIN chunks_vec v
            WHERE v.ChunkKey = c.MemoryId || ':' || c.ChunkIndex
            {(hasTagFilter ? $"AND {TagsMatchFunction}(c.Tags, @tagFilter)" : string.Empty)}
            ORDER BY distance
            """;
        cmd.Parameters.AddWithValue("@queryVector", VectorToBlob(queryVector));
        if (hasTagFilter)
        {
            cmd.Parameters.AddWithValue("@tagFilter", JsonSerializer.Serialize(tags));
        }

        var resultsByMemory = new Dictionary<string, SearchResult>();

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var memoryId = reader.GetString(0);

            // Rows arrive best-first, so the first row of a memory is its best chunk.
            // A NULL distance means an unscorable (zero) vector.
            if (resultsByMemory.ContainsKey(memoryId) || reader.IsDBNull(1))
            {
                continue;
            }

            // Cosine distance is in [0, 2]; similarity = 1 - distance is in [-1, 1].
            float score = 1.0f - reader.GetFloat(1);
            if (minScore.HasValue && score < minScore.Value)
            {
                continue;
            }

            resultsByMemory[memoryId] = new SearchResult
            {
                MemoryId = memoryId,
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                Content = string.Empty, // Will be filled below
                Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
                CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(5)),
                Score = score,
            };
        }

```

(`CROSS JOIN` forces `chunks` as the outer loop so each surviving chunk is fetched by vec0 point lookup on `ChunkKey`; `EXPLAIN QUERY PLAN` shows `SCAN c` then `SCAN v VIRTUAL TABLE INDEX 3:2!___`.)

- [ ] **Step 5: Update the store contract doc**

In `lib/memory-mcp/src/MemoryMcp.Core/Storage/IMemoryStore.cs`, replace the `SearchAsync` summary:

```csharp
    /// <summary>
    /// Searches for the most similar chunks using vector similarity.
    /// Returns results grouped by memory ID with the best score per memory.
    /// </summary>
```

with:

```csharp
    /// <summary>
    /// Exact cosine search: scores every chunk that passes the filters (filters are applied before
    /// scoring, so any filter keeps exact recall) and returns memories grouped by ID with the best
    /// chunk score per memory. Ranking is always pure vector similarity.
    /// </summary>
```

- [ ] **Step 6: Run the whole project**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj
```

Expected: `Passed!  - Failed: 0, Passed: 100, Skipped: 0, Total: 100`.

- [ ] **Step 7: Build the whole memory-mcp solution (integration tests must still compile)**

```powershell
dotnet build lib\memory-mcp\MemoryMcp.slnx
```

Expected: `Build succeeded` with no new `CS`/`CA` warnings in `MemoryMcp.Core` (pre-existing `xUnit1051` warnings in old tests are fine).

- [ ] **Step 8: Commit (submodule)**

```powershell
git -C lib\memory-mcp add -A src/MemoryMcp.Core tests/MemoryMcp.Core.Tests
git -C lib\memory-mcp commit -m "fix: exact search with tag filter applied before scoring" -m "The limit*10 KNN post-filter lost most matches under selective filters. Candidate sets for threshold callers are unchanged (tested against the old algorithm)." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: memory-mcp docs and push the branch

**Files:**
- Modify: `lib/memory-mcp/README.md` (§ Search Pipeline, test table, Project Structure)

- [ ] **Step 1: Update the search pipeline**

In `lib/memory-mcp/README.md` replace:

```markdown
1. Embed the query text with Ollama.
2. Query sqlite-vec for the nearest `limit * 10` chunk vectors (overfetch to account for grouping and filtering).
3. Convert cosine distance to similarity: `score = 1 - distance`.
4. Apply `minScore` and tag filters.
5. Group by memory ID, keeping the best chunk score per memory.
6. Sort by score descending, return top `limit` results with content loaded from files.
```

with:

```markdown
1. Embed the query text with Ollama.
2. Select the chunks that pass the tag filter (case-insensitive, any tag matches) and score **every** one of them with `vec_distance_cosine` — an exact scan, so a selective filter never loses matches.
3. Convert cosine distance to similarity: `score = 1 - distance`, and drop scores below `minScore`.
4. Group by memory ID, keeping the best chunk score per memory.
5. Sort by score descending, return top `limit` results with content loaded from files.

`IMemoryService.FindSimilarAsync` is the dense-only variant for threshold checks (duplicate guard, consolidation): it never takes tags and will never fuse keyword ranking, so its threshold always means cosine similarity.

### Deletion

Deletes are physical: sqlite-vec ≥ 0.1.9 zeroes the deleted vector slot, `PRAGMA secure_delete = ON` zeroes deleted rows, and the WAL (if enabled) is truncated after every delete. On first start the store rebuilds vector tables written by older sqlite-vec versions and runs `VACUUM` (`PRAGMA user_version = 1` marks it done).

### sqlite-vec binaries

The NuGet `sqlite-vec` package stops at 0.1.7-alpha.2.1, so the official release binaries are vendored in `src/MemoryMcp.Core/runtimes/<rid>/native/` (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64) with their licenses and `SQLITE-VEC-VERSION.txt`. To update: `pwsh scripts/update-sqlite-vec.ps1 -Version <x.y.z>` (needs `gh`); it verifies every archive against the release `checksums.txt` and GitHub's asset digest. The store refuses to start with a version older than 0.1.9.
```

- [ ] **Step 2: Update the test table**

Replace:

```markdown
| `MemoryMcp.Core.Tests` | 49 | Chunking, MemoryService orchestration, SqliteVec store CRUD |
| `MemoryMcp.Tests` | 21 | MCP tool response formatting, tag parsing, content truncation |
```

with:

```markdown
| `MemoryMcp.Core.Tests` | 100 | Chunking, MemoryService orchestration, SqliteVec store CRUD, byte-level erasure, exact filtered search |
| `MemoryMcp.Tests` | 26 | MCP tool response formatting, tag parsing, content truncation |
```

- [ ] **Step 3: Update the project structure**

Replace:

```markdown
    run.sh / run.ps1                 Publish + start server (--rebuild to force)
```

with:

```markdown
    run.sh / run.ps1                 Publish + start server (--rebuild to force)
    update-sqlite-vec.ps1            Vendor + verify sqlite-vec release binaries
```

and replace:

```markdown
        SqliteVecMemoryStore.cs      SQLite + sqlite-vec + file storage
```

with:

```markdown
        SqliteVecMemoryStore.cs      SQLite + sqlite-vec + file storage
        SqliteVecExtension.cs        Loads vendored vec0, enforces >= 0.1.9
      runtimes/                      Vendored sqlite-vec binaries per RID
```

- [ ] **Step 4: Commit and push the submodule branch**

```powershell
git -C lib\memory-mcp add README.md
git -C lib\memory-mcp commit -m "docs: exact search, physical deletion, vendored sqlite-vec" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git -C lib\memory-mcp push -u origin feat/inc0-correctness
git -C lib\memory-mcp rev-parse HEAD
```

Expected: push creates `feat/inc0-correctness` on `yury-opolev/memory-mcp`; note the printed SHA (the submodule commit Cortex will record).

---

### Task 9: Cortex — consolidation and compaction on `FindSimilarAsync` (+ submodule pointer)

Both classes are tested together because the compaction tests drive the real `MemoryConsolidationService`.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Memory/MemoryConsolidationService.cs:149-154`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/MemoryCompactionService.cs:232-237`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/MemoryConsolidationServiceTests.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/MemoryCompactionServiceTests.cs`
- Modify: `lib/memory-mcp` (gitlink)

- [ ] **Step 1: Write the failing routing test**

In `tests/Cortex.Contained.Agent.Host.Tests/MemoryConsolidationServiceTests.cs`, insert directly above `    // ── ADD (no similar memories) ──────────────────────────────────────`:

```csharp
    // ── Candidate lookup ───────────────────────────────────────────────

    [Fact]
    public async Task ConsolidateAsync_LooksUpCandidatesWithDenseFindSimilar()
    {
        SetupSearchEmpty();
        SetupIngest();

        await _sut.ConsolidateAsync(MakeFact("User likes cats"), Model, "conv-1", null, CancellationToken.None);

        await _memoryService.Received(1).FindSimilarAsync(
            "User likes cats", SimilarMemoryLimit, SimilarityThreshold, Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, default, default);
    }

```

and change the two mock helpers to the new API — replace:

```csharp
    private void SetupSearchEmpty()
    {
        _memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(),
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
    }

    private void SetupSearchWithResults(params SearchResult[] results)
    {
        _memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(),
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(results.ToList());
    }
```

with:

```csharp
    private void SetupSearchEmpty()
    {
        _memoryService.FindSimilarAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
    }

    private void SetupSearchWithResults(params SearchResult[] results)
    {
        _memoryService.FindSimilarAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float>(), Arg.Any<CancellationToken>())
            .Returns(results.ToList());
    }
```

- [ ] **Step 2: Point the compaction test mocks at `FindSimilarAsync`**

In `tests/Cortex.Contained.Agent.Host.Tests/MemoryCompactionServiceTests.cs` make these exact replacements:

1. In `SetupDefaultMocks` (and its doc comment, "internal `<c>SearchAsync</c>` and" → "internal `<c>FindSimilarAsync</c>` and"):

```csharp
        _memoryService.SearchAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(new List<SearchResult>());
```

→

```csharp
        _memoryService.FindSimilarAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new List<SearchResult>());
```

(`default!` would now be ambiguous between the `string` and `float[]` overloads.)

2. In `CompactionSweep_NoDuplicates_DoesNotMerge`:

```csharp
        _memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(),
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
```

→

```csharp
        _memoryService.FindSimilarAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
```

3. Every remaining stub/assertion of the shape

```csharp
        _memoryService.SearchAsync(
            "<text>", Arg.Any<int>(), <threshold>,
            Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
```

becomes

```csharp
        _memoryService.FindSimilarAsync(
            "<text>", Arg.Any<int>(), <threshold>, Arg.Any<CancellationToken>())
```

for these (text, threshold) pairs: (`"User likes cats"`, `0.7f`), (`"User loves cats"`, `SimilarityThreshold`), (`"Content"`, `0.7f`), (`"Same content"`, `SimilarityThreshold`), (`"Content A"`, `0.7f`), (`"Content A duplicate"`, `SimilarityThreshold`), and the final `await _memoryService.DidNotReceive().SearchAsync("Content A duplicate", Arg.Any<int>(), 0.7f, ...)` → `await _memoryService.DidNotReceive().FindSimilarAsync("Content A duplicate", Arg.Any<int>(), 0.7f, Arg.Any<CancellationToken>());`.

4. Comment in `CompactionSweep_DuplicateCluster_CallsConsolidation`: "consolidation's internal SearchAsync needs a safe return" → "consolidation's internal FindSimilarAsync needs a safe return".

Check nothing is left:

```powershell
Select-String -Path tests\Cortex.Contained.Agent.Host.Tests\MemoryCompactionServiceTests.cs -Pattern "SearchAsync\(" | Where-Object { $_.Line -notmatch "FindSimilarAsync" }
```

Expected: no output.

- [ ] **Step 3: Run to verify they fail**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests\Cortex.Contained.Agent.Host.Tests.csproj --filter "FullyQualifiedName~MemoryConsolidationServiceTests|FullyQualifiedName~MemoryCompactionServiceTests"
```

Expected: FAILs — `ConsolidateAsync_LooksUpCandidatesWithDenseFindSimilar` (received no `FindSimilarAsync` call) plus the tests that rely on the stubs (production code still calls `SearchAsync`, which is now unstubbed).

- [ ] **Step 4: Switch consolidation**

In `src/Cortex.Contained.Agent.Host/Memory/MemoryConsolidationService.cs` (`ConsolidateAsync`) replace:

```csharp
        // Search for semantically similar existing memories
        var similarMemories = await this.memoryService.SearchAsync(
            fact.Content,
            SimilarMemoryLimit,
            SimilarityThreshold,
            cancellationToken: cancellationToken).ConfigureAwait(false);
```

with:

```csharp
        // Dense-only lookup: the threshold is a cosine similarity and must stay one.
        var similarMemories = await this.memoryService.FindSimilarAsync(
            fact.Content,
            SimilarMemoryLimit,
            SimilarityThreshold,
            cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 5: Switch compaction**

In `src/Cortex.Contained.Agent.Host/Memory/MemoryCompactionService.cs` (`RunCompactionAsync`) replace:

```csharp
                // Search for similar memories
                var similar = await this.memoryService.SearchAsync(
                    memory.Content,
                    CompactionSearchLimit,
                    this.options.SimilarityThreshold,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
```

with:

```csharp
                // Dense-only near-duplicate lookup (pure cosine threshold)
                var similar = await this.memoryService.FindSimilarAsync(
                    memory.Content,
                    CompactionSearchLimit,
                    this.options.SimilarityThreshold,
                    cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 6: Run to verify they pass**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests\Cortex.Contained.Agent.Host.Tests.csproj --filter "FullyQualifiedName~MemoryConsolidationServiceTests|FullyQualifiedName~MemoryCompactionServiceTests"
```

Expected: `Failed: 0`, build warning-free (`TreatWarningsAsErrors`).

- [ ] **Step 7: Commit (Cortex, including the submodule pointer)**

```powershell
git add lib/memory-mcp src/Cortex.Contained.Agent.Host/Memory/MemoryConsolidationService.cs src/Cortex.Contained.Agent.Host/Memory/MemoryCompactionService.cs tests/Cortex.Contained.Agent.Host.Tests/MemoryConsolidationServiceTests.cs tests/Cortex.Contained.Agent.Host.Tests/MemoryCompactionServiceTests.cs
git status --short
git commit -m "fix(memory): consolidation and compaction use dense-only FindSimilarAsync" -m "Bumps lib/memory-mcp to feat/inc0-correctness (sqlite-vec 0.1.9, secure_delete, exact filtered search, FindSimilarAsync)." -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Expected `git status --short` before committing: only the five staged paths (`M  lib/memory-mcp` among them) plus the untouched local ` M version.json`.

---

### Task 10: Cortex — subagent context injection on `FindSimilarAsync`

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Agent/SubagentExecutor.cs:152-154, 193-206`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/SubagentExecutorMemoryContextTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/Cortex.Contained.Agent.Host.Tests/SubagentExecutorMemoryContextTests.cs`:

```csharp
using System.Reflection;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Subagent context injection must use the dense-only <see cref="IMemoryService.FindSimilarAsync(string, int, float, CancellationToken)"/>:
/// its 0.3 score floor is a cosine similarity and must keep that meaning when memory_search turns hybrid.
/// </summary>
public class SubagentExecutorMemoryContextTests
{
    [Fact]
    public async Task SearchMemoriesAsync_UsesDenseFindSimilarPerQuery()
    {
        var memoryService = Substitute.For<IMemoryService>();
        memoryService.FindSimilarAsync("user editor preferences", 3, 0.3f, Arg.Any<CancellationToken>())
            .Returns([new SearchResult { MemoryId = "m-1", Content = "Prefers Rider", Score = 0.8f }]);
        memoryService.FindSimilarAsync("deployment workflow", 3, 0.3f, Arg.Any<CancellationToken>())
            .Returns([new SearchResult { MemoryId = "m-2", Content = "Deploys with Self-Update", Score = 0.6f }]);
        var executor = NewExecutor(memoryService);
        var method = typeof(SubagentExecutor).GetMethod("SearchMemoriesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var task = (Task<List<SearchResult>>)method.Invoke(
            executor, [new List<string> { "user editor preferences", "deployment workflow" }, 6, 0.3f, CancellationToken.None])!;
        var results = await task;

        Assert.Equal(["m-1", "m-2"], results.Select(r => r.MemoryId));
        await memoryService.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, default, default);
    }

    private static SubagentExecutor NewExecutor(IMemoryService memoryService)
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.DefaultModel.Returns("test-model");

        return new SubagentExecutor(
            new SubagentRunnerRegistry(1, NullLogger<SubagentRunnerRegistry>.Instance),
            Substitute.For<ILlmClient>(),
            modelProvider,
            AppContext.BaseDirectory,
            NullLogger<SubagentExecutor>.Instance,
            memoryService);
    }
}
```

(The private-method reflection pattern matches `SystemPromptCharacterizationTests.SubagentExecutor_DefaultPrompt_IncludesAutonomousRelayGuidance`.)

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests\Cortex.Contained.Agent.Host.Tests.csproj --filter "FullyQualifiedName~SubagentExecutorMemoryContextTests"
```

Expected: FAIL — `System.Reflection.TargetParameterCountException: Parameter count mismatch.` (the method still has the `tags` parameter).

- [ ] **Step 3: Implement**

In `src/Cortex.Contained.Agent.Host/Agent/SubagentExecutor.cs` (`RetrieveSubagentContextAsync`) replace:

```csharp
            // Step 2: Execute memory searches across the entire memory store (no tag filter)
            var memoryCandidates = await this.SearchMemoriesAsync(
                queries, MaxMemoryResults, MinMemoryScore, tags: null, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
            // Step 2: Dense similarity lookups across the entire memory store (no tag filter)
            var memoryCandidates = await this.SearchMemoriesAsync(
                queries, MaxMemoryResults, MinMemoryScore, cancellationToken).ConfigureAwait(false);
```

replace the signature:

```csharp
    private async Task<List<MemoryMcp.Core.Models.SearchResult>> SearchMemoriesAsync(
        List<string> queries, int maxResults, float minScore,
        List<string>? tags, CancellationToken cancellationToken)
```

with:

```csharp
    private async Task<List<MemoryMcp.Core.Models.SearchResult>> SearchMemoriesAsync(
        List<string> queries, int maxResults, float minScore, CancellationToken cancellationToken)
```

and replace the call inside the loop:

```csharp
            var results = await this.memoryService.SearchAsync(
                query, 3, minScore, tags, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
            var results = await this.memoryService.FindSimilarAsync(
                query, 3, minScore, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Run to verify it passes (with the existing subagent prompt tests)**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests\Cortex.Contained.Agent.Host.Tests.csproj --filter "FullyQualifiedName~SubagentExecutorMemoryContextTests|FullyQualifiedName~SystemPromptCharacterizationTests"
```

Expected: `Failed: 0`.

- [ ] **Step 5: Confirm no threshold caller is left on `SearchAsync`**

```powershell
Select-String -Path src\Cortex.Contained.Agent.Host\**\*.cs -Pattern "memoryService\.SearchAsync\(" | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
```

Expected: only the user-facing searches remain — `Hubs\AgentHub.cs` (`SearchMemories`), `Memory\MemoryManagementService.cs` (`SearchAsync` passthrough) and `Tools\BuiltIn\MemorySearchTool.cs` (`memory_search`). These keep `SearchAsync` on purpose.

- [ ] **Step 6: Commit (Cortex)**

```powershell
git add src/Cortex.Contained.Agent.Host/Agent/SubagentExecutor.cs tests/Cortex.Contained.Agent.Host.Tests/SubagentExecutorMemoryContextTests.cs
git commit -m "fix(subagents): memory context injection uses dense-only FindSimilarAsync" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 11: Verification — test gates and the agent image

**Files:** none (verification only)

- [ ] **Step 1: memory-mcp unit suites**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj
dotnet test lib\memory-mcp\tests\MemoryMcp.Tests\MemoryMcp.Tests.csproj
```

Expected: `Passed: 100` and `Passed: 26`, 0 failed.

- [ ] **Step 2: Optional — integration suites (needs local Ollama with `qwen3-embedding:0.6b`)**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.IntegrationTests\MemoryMcp.Core.IntegrationTests.csproj
```

Expected: pass, or skip automatically when Ollama is unreachable. `DuplicateGuardEvalTests` and `SearchQualityTests` exercise the new exact scan with real embeddings.

- [ ] **Step 3: Cortex Agent.Host test project (the deploy gate's relevant slice)**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests\Cortex.Contained.Agent.Host.Tests.csproj
```

Expected: `Failed: 0`. If anything outside memory/subagent tests fails, compare with a run on the commit before Task 9 before investigating (pre-existing flakes are not part of this increment).

- [ ] **Step 4: Confirm the native library is published with the agent**

```powershell
dotnet publish src\Cortex.Contained.Agent.Host\Cortex.Contained.Agent.Host.csproj -c Release -o artifacts\inc0-publish /p:UseAppHost=false
Get-ChildItem artifacts\inc0-publish\runtimes -Recurse -Filter "vec0*" | ForEach-Object FullName
Remove-Item -Recurse -Force artifacts\inc0-publish
```

Expected: `runtimes\linux-x64\native\vec0.so` (plus the other 4 RIDs) in the publish output.

- [ ] **Step 5: Build the agent image (`final` stage is enough) and load vec0 inside it**

```powershell
docker build -t cortex-agent:inc0-verify --target final -f src\Cortex.Contained.Agent.Host\Dockerfile .
docker run --rm --entrypoint sha256sum cortex-agent:inc0-verify /app/runtimes/linux-x64/native/vec0.so
docker run --rm --entrypoint python3 cortex-agent:inc0-verify -c "import sqlite3; c=sqlite3.connect(':memory:'); c.enable_load_extension(True); c.load_extension('/app/runtimes/linux-x64/native/vec0'); print(c.execute('select vec_version()').fetchone()[0])"
docker rmi cortex-agent:inc0-verify
```

Expected: `5923730861b86c707cca5602b5f91092f9e52a46706dbc6e269fd4bb9c4498e8  /app/runtimes/linux-x64/native/vec0.so` (matches the manifest) and `v0.1.9`. No Dockerfile change is needed: the `COPY lib/memory-mcp/src/MemoryMcp.Core/` line already brings `runtimes/`.

---

### Task 12: Merge the submodule PR, final pointer, docs, deploy

**Files:**
- Modify: `lib/memory-mcp` (gitlink → merge commit on `main`)
- Modify: `docs/superpowers/research/2026-11-21-memory-search-scale-path.md` ("Current-code defects found (to fix)")

- [ ] **Step 1: Open the submodule PR**

```powershell
gh pr create -R yury-opolev/memory-mcp --base main --head feat/inc0-correctness --title "Correctness: sqlite-vec 0.1.9, physical deletion, exact filtered search, FindSimilarAsync" --body "Increment 0 of the Cortex episodic-history-memory roadmap.`n`n- Vendor official sqlite-vec v0.1.9 binaries (NuGet stops at 0.1.7-alpha.2.1, whose DELETE only clears a validity bit); scripts/update-sqlite-vec.ps1 verifies checksums.txt + GitHub asset digests.`n- PRAGMA secure_delete=ON; WAL truncate after deletes; one-time vector-table rebuild + VACUUM (user_version 1).`n- Exact search with tag filter applied before scoring (was limit*10 KNN then post-filter).`n- Dense-only IMemoryService.FindSimilarAsync; duplicate guard uses it.`n`nGate tests: byte-level erasure (vector, metadata, WAL, legacy residue), filtered recall = brute force, candidate sets = previous KNN for (5,0.3) (10,0.7) (3,0.3) (5,0.9)."
```

Expected: prints the PR URL.

- [ ] **Step 2: Merge it with a merge commit (keeps the branch commits Cortex already references reachable)**

```powershell
gh pr merge feat/inc0-correctness -R yury-opolev/memory-mcp --merge --delete-branch
gh pr view feat/inc0-correctness -R yury-opolev/memory-mcp --json state,mergeCommit --jq '.state + " " + .mergeCommit.oid'
```

Expected: `MERGED <sha>`.

- [ ] **Step 3: Point Cortex at the merge commit**

```powershell
git -C lib\memory-mcp fetch origin
git -C lib\memory-mcp switch main
git -C lib\memory-mcp reset --hard origin/main
git -C lib\memory-mcp log --oneline -1
git -C lib\memory-mcp branch -D feat/inc0-correctness
```

Expected: `log` shows `Merge pull request #<n> from yury-opolev/feat/inc0-correctness`. (If `switch main` reports no local `main`, use `git -C lib\memory-mcp switch -c main origin/main`.)

- [ ] **Step 4: Mark the research defects fixed**

In `docs/superpowers/research/2026-11-21-memory-search-scale-path.md` replace the heading line:

```markdown
## Current-code defects found (to fix)
```

with:

```markdown
## Current-code defects found (fixed in increment 0)

Both defects below are fixed in `lib/memory-mcp` (merged PR "Correctness: sqlite-vec 0.1.9, physical deletion, exact filtered search, FindSimilarAsync"): sqlite-vec v0.1.9 is vendored, `secure_delete` is on, the WAL is truncated after deletes, legacy vector tables are rebuilt once and vacuumed, and search is an exact scan with filters applied before scoring.
```

- [ ] **Step 5: Re-run the quick gate on the merged submodule and commit**

```powershell
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests\MemoryMcp.Core.Tests.csproj
dotnet test tests\Cortex.Contained.Agent.Host.Tests\Cortex.Contained.Agent.Host.Tests.csproj --filter "FullyQualifiedName~MemoryConsolidationServiceTests|FullyQualifiedName~MemoryCompactionServiceTests|FullyQualifiedName~SubagentExecutorMemoryContextTests"
git add lib/memory-mcp docs/superpowers/research/2026-11-21-memory-search-scale-path.md
git commit -m "chore: point lib/memory-mcp at merged increment-0 fixes" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Expected: both test runs `Failed: 0`; commit contains `M lib/memory-mcp` and the doc. Do **not** push the Cortex branch as part of this plan unless the owner asks.

- [ ] **Step 6: Optional — deploy and confirm the one-time rebuild ran**

Only when the owner asks to deploy. The first start rebuilds the production vector table and vacuums `memory.db` (forward-only; an image rollback keeps working because vec0 0.1.7-alpha.2.1 reads 0.1.9 tables — verified).

```powershell
.\scripts\Self-Update.ps1 -Schedule -SkipPull
```

After the restart (~45 s plus build time):

```powershell
docker logs cortex-agent 2>&1 | Select-String "sqlite-vec: v0.1.9|Rebuilt the vector table"
```

Expected: one `Memory store initialized ... sqlite-vec: v0.1.9` line and, on the first start only, one `Rebuilt the vector table with sqlite-vec >= 0.1.9 and vacuumed /app/state/memory/memory.db.` line.

---

## Self-review

- **Spec coverage (row 0 of §4):** sqlite-vec ≥ 0.1.9 → Tasks 2–3 (+ Task 5 for pre-existing residue); `secure_delete` (+ WAL truncate) → Task 4; filters inside the scan → Task 7; dense-only `FindSimilarAsync` + consolidation/compaction/subagent/duplicate guard → Tasks 6, 9, 10. Gates: "a delete leaves no vector bytes" → `DeleteMemoryAsync_VectorBytesAreErasedFromDatabaseFile` (+ metadata, WAL, legacy tests); "filtered recall equals exact" → `SearchAsync_SelectiveTagFilter_EqualsBruteForceOverFilteredSet`; "consolidation candidate sets unchanged" → `FindSimilarAsync_CandidateSets_MatchPreviousKnnSearch` (4 caller parameter sets). FTS5 `secure-delete`: no FTS table exists in MemoryMcp.Core yet — nothing to enable (increment 1 owns FTS).
- **Type consistency:** `SqliteVecExtension.Load/ResolvePath/EnsureSupported/MinimumVersion`, `DatabaseBytes.Contains/Of`, `FindSimilarAsync(string|float[], int, float, CancellationToken)`, `TagsMatchFunction`/`TagsMatch`, `CreateVectorTableSql`/`RebuildLegacyVectorTableAsync` are used with the same names and signatures in every task.
- **Test counts:** Core.Tests 80 → 88 (Task 3: +1 erasure, +7 loader) → 90 (Task 4) → 91 (Task 5) → 94 (Task 6) → 100 (Task 7).

## Risks and decisions

| Item | Decision / mitigation |
|---|---|
| No NuGet ≥ 0.1.9 | Vendor official release binaries; two GitHub-hosted hashes checked; version guard at startup. Updating = re-run the script. |
| Binary blobs in the submodule (~0.9 MB) | Acceptable; `.gitattributes` marks them binary. Agent publish output also carries the non-Linux RIDs (~0.6 MB) — harmless. |
| `win-arm64` / other RIDs | Not published upstream; startup fails with a clear `FileNotFoundException` (same as the old package). |
| Exact scan cost | O(chunks) per search; ~350 chunks today. Scale-path record covers int8/SIMD later. |
| One-time rebuild + VACUUM on first start | Runs inside a transaction (VACUUM after commit); tiny DB. Rollback-compatible with the old image. |
| Content files | Still unlinked, not overwritten (out of scope; filesystem-dependent). |
| Cortex consumers outside this repo's tests | `IMemoryService` gains two members; only NSubstitute fakes exist (checked: no hand-written implementations in Cortex or memory-mcp). |
