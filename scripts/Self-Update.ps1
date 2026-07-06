<#
.SYNOPSIS
    Cortex self-update pipeline — pull -> build -> test-gate -> verify -> deploy -> runtime-verify
    -> auto-rollback-on-fail -> report. Designed to run DETACHED (from a one-shot Scheduled Task,
    i.e. OUTSIDE the Bridge's Job Object) so it survives the Bridge/coda shutdown it triggers.

.DESCRIPTION
    DRY-RUN BY DEFAULT: without -Apply this runs the safe half only (pull/build/test/verify the
    signed MSIX against artifacts/update-manifest.json) and STOPS before any deploy. Pass -Apply to
    actually recreate the containers, install the MSIX (force-shutting the old Bridge), relaunch,
    verify /health returns the target version, and roll back to the last-known-good on failure.

    See docs/superpowers/specs/2026-07-06-self-update-design.md.

.PARAMETER Apply
    Arm the deploy + rollback. Without it the script is a no-op past verification (dry run).

.PARAMETER Ref
    Git ref to update to. Default: the current branch's upstream (usually origin/main).

.PARAMETER SkipBuild
    Skip git pull + Build-All and verify/deploy the EXISTING artifacts/update-manifest.json. For
    exercising the verify/deploy logic without a multi-minute rebuild.

.PARAMETER SkipTests
    Skip the test-gate. NOT recommended — a red build is exactly what rollback exists to avoid.
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$Ref = '',
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [string]$CertThumbprint = $env:CORTEX_SIGNING_THUMBPRINT,
    [string]$HealthUrl = 'http://localhost:5080/health',
    [string]$Aumid = 'Cortex.Contained.Launcher_hnfrhv5dkzjbe!CortexLauncher',
    [int]$HealthTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$manifestPath = Join-Path $artifacts 'update-manifest.json'
$statusPath = Join-Path $artifacts 'update-status.json'
$lockPath = Join-Path $artifacts '.self-update.lock'
$lkgMsix = Join-Path $artifacts 'last-known-good.msix'
$rollbackImageTag = 'cortex-agent:rollback'
$log = [System.Collections.Generic.List[string]]::new()

function Say([string]$msg, [string]$color = 'Cyan') {
    $line = "[self-update] $msg"
    Write-Host $line -ForegroundColor $color
    $log.Add(("{0}  {1}" -f (Get-Date).ToUniversalTime().ToString('s'), $msg))
}

function Get-InstalledVersion {
    (Get-AppxPackage -Name '*Cortex*' -ErrorAction SilentlyContinue | Select-Object -First 1).Version
}

function Test-HealthAt([string]$expectVersion) {
    # Poll /health until it reports healthy on the expected version, within the budget.
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $h = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 5 -ErrorAction Stop
            if ($h.healthy -eq $true -and ($expectVersion -eq '' -or $h.version -like "$expectVersion*")) {
                return $true
            }
        } catch { }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Write-Status([bool]$ok, [string]$from, [string]$to, [bool]$rolledBack, [string]$reason) {
    $status = [ordered]@{
        ok          = $ok
        fromVersion = $from
        toVersion   = $to
        rolledBack  = $rolledBack
        reason      = $reason
        timestamp   = (Get-Date).ToUniversalTime().ToString('o')
        log         = $log.ToArray()
    }
    $status | ConvertTo-Json -Depth 5 | Set-Content -Path $statusPath -Encoding UTF8
    Say "status written: ok=$ok rolledBack=$rolledBack -> $statusPath" ($(if ($ok) { 'Green' } else { 'Yellow' }))
}

# --- Concurrency guard -------------------------------------------------------
if (Test-Path $lockPath) {
    $age = (Get-Date) - (Get-Item $lockPath).LastWriteTime
    if ($age.TotalMinutes -lt 60) {
        throw "A self-update appears to be in progress (lock < 60m old: $lockPath). Refusing to stack."
    }
    Say "stale lock ($([int]$age.TotalMinutes)m) — reclaiming" 'Yellow'
}
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Set-Content -Path $lockPath -Value ((Get-Date).ToUniversalTime().ToString('o')) -Encoding UTF8

try {
    $fromVersion = Get-InstalledVersion
    Say "current installed version: $fromVersion"

    # --- 1. Resolve sources --------------------------------------------------
    if (-not $SkipBuild) {
        if (-not $Ref) {
            $Ref = (& git -C $repoRoot rev-parse --abbrev-ref '@{u}' 2>$null)
            if (-not $Ref) { $Ref = 'origin/main' }
        }
        Say "pulling sources: $Ref"
        & git -C $repoRoot fetch --all --prune 2>&1 | Out-Null
        & git -C $repoRoot merge --ff-only $Ref 2>&1 | ForEach-Object { Say "git: $_" 'Gray' }
        if ($LASTEXITCODE -ne 0) { throw "git merge --ff-only $Ref failed (local commits? resolve manually)." }
        & git -C $repoRoot submodule update --init --recursive 2>&1 | Out-Null
    } else {
        Say "SkipBuild — using existing artifacts/manifest" 'Yellow'
    }

    # --- 2. Build ------------------------------------------------------------
    if (-not $SkipBuild) {
        Say "building (Build-All)..."
        $buildArgs = @{}
        if ($CertThumbprint) { $buildArgs['CertThumbprint'] = $CertThumbprint }
        & (Join-Path $PSScriptRoot 'Build-All.ps1') @buildArgs
        if ($LASTEXITCODE -ne 0) { throw "Build-All failed." }
    }

    # --- 3. Test-gate --------------------------------------------------------
    if (-not $SkipTests -and -not $SkipBuild) {
        Say "running tests (gate)..."
        & dotnet test (Join-Path $repoRoot 'cortex-contained.sln') --nologo 2>&1 | ForEach-Object { if ($_ -match 'Passed!|Failed!|error') { Say "test: $_" 'Gray' } }
        if ($LASTEXITCODE -ne 0) { throw "TEST GATE FAILED — aborting before any deploy." }
    } elseif ($SkipTests) {
        Say "SkipTests — test gate bypassed (NOT recommended)" 'Yellow'
    }

    # --- 4. Verify the manifest (the build->deploy contract) -----------------
    if (-not (Test-Path $manifestPath)) { throw "No build manifest at $manifestPath." }
    $m = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $targetVersion = $m.version
    $msixFile = Join-Path $repoRoot ($m.msix.path -replace '/', '\')
    Say "target version $targetVersion (commit $($m.gitCommit)); msix $($m.msix.path)"

    if (-not (Test-Path $msixFile)) { throw "Manifest MSIX not found: $msixFile" }
    $actualSha = (Get-FileHash -Algorithm SHA256 -Path $msixFile).Hash.ToLowerInvariant()
    if ($actualSha -ne $m.msix.sha256.ToLowerInvariant()) { throw "MSIX sha256 mismatch — refusing (stale/corrupt)." }
    $sig = Get-AuthenticodeSignature -FilePath $msixFile
    if ($sig.Status -ne 'Valid') { throw "MSIX signature not Valid ($($sig.Status)) — refusing." }
    $sigThumb = $sig.SignerCertificate.Thumbprint
    if ($sigThumb -ne $m.msix.certThumbprint) { throw "MSIX signer thumbprint $sigThumb != manifest $($m.msix.certThumbprint) — refusing." }
    Say "manifest verified: sha256 OK, signature Valid, thumbprint OK" 'Green'

    # --- 5. Gate: dry run stops here -----------------------------------------
    if (-not $Apply) {
        Say "DRY RUN — verified and ready. Pass -Apply to deploy $fromVersion -> $targetVersion." 'Green'
        Write-Status -ok $true -from $fromVersion -to $targetVersion -rolledBack $false -reason 'dry-run'
        return
    }

    # --- 6. Snapshot last-known-good (for rollback) --------------------------
    $lkgSaved = $false
    $prevMsix = Join-Path $artifacts "CortexLauncher-$fromVersion.msix"
    if (Test-Path $prevMsix) { Copy-Item $prevMsix $lkgMsix -Force; $lkgSaved = $true; Say "last-known-good MSIX saved ($fromVersion)" }
    else { Say "no MSIX for current version $fromVersion in artifacts — MSIX rollback unavailable" 'Yellow' }
    try { & docker tag cortex-agent:latest $rollbackImageTag 2>$null; Say "tagged current agent image as $rollbackImageTag" } catch { Say "could not snapshot agent image for rollback" 'Yellow' }

    # --- 7. Deploy: containers, then MSIX ------------------------------------
    Say "recreating containers ($($m.images.'cortex-agent'), $($m.images.'voice-id'))..."
    & docker compose -f (Join-Path $repoRoot 'docker-compose.yml') up -d --force-recreate --no-deps cortex-agent voice-id 2>&1 | ForEach-Object { Say "docker: $_" 'Gray' }

    Say "installing MSIX $targetVersion (stops the Bridge)..."
    Add-AppxPackage -Path $msixFile -ForceUpdateFromAnyVersion -ForceApplicationShutdown
    Say "relaunching Bridge via $Aumid"
    Start-Process "shell:AppsFolder\$Aumid"

    # --- 8. Runtime verify ---------------------------------------------------
    Say "verifying /health reports $targetVersion..."
    if (Test-HealthAt $targetVersion) {
        Say "HEALTHY on $targetVersion — update complete" 'Green'
        Write-Status -ok $true -from $fromVersion -to $targetVersion -rolledBack $false -reason 'deployed'
        return
    }

    # --- 9. Rollback ---------------------------------------------------------
    Say "target did NOT come healthy within ${HealthTimeoutSeconds}s — ROLLING BACK" 'Red'
    $rolledBack = $false
    if ($lkgSaved) {
        try {
            & docker tag $rollbackImageTag cortex-agent:latest 2>$null
            & docker compose -f (Join-Path $repoRoot 'docker-compose.yml') up -d --force-recreate --no-deps cortex-agent 2>&1 | Out-Null
            Add-AppxPackage -Path $lkgMsix -ForceUpdateFromAnyVersion -ForceApplicationShutdown
            Start-Process "shell:AppsFolder\$Aumid"
            $rolledBack = Test-HealthAt $fromVersion
            Say ("rollback " + $(if ($rolledBack) { 'HEALTHY on ' + $fromVersion } else { 'did NOT come healthy — MANUAL INTERVENTION NEEDED' })) $(if ($rolledBack) { 'Yellow' } else { 'Red' })
        } catch { Say "rollback error: $_" 'Red' }
    } else {
        Say "no last-known-good MSIX — cannot roll back automatically. MANUAL INTERVENTION NEEDED." 'Red'
    }
    Write-Status -ok $false -from $fromVersion -to $targetVersion -rolledBack $rolledBack -reason 'health-gate-failed'
    throw "Self-update failed its health gate (rolledBack=$rolledBack)."
}
finally {
    Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
}
