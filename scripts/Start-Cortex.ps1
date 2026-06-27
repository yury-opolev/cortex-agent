<#
.SYNOPSIS
    Starts the Cortex stack (Agent container + Bridge).

.DESCRIPTION
    Builds the Agent container image, starts it alongside Ollama,
    then starts the Bridge on the host. Press Ctrl+C to stop both.

.PARAMETER HubToken
    Shared authentication token. Defaults to "dev-token-change-me".

.PARAMETER BridgeOnly
    Skip the container image build step. Uses the existing image.
    Useful for fast iteration on Bridge-only changes.

.EXAMPLE
    .\Start-Cortex.ps1
    # Builds and starts everything

.EXAMPLE
    .\Start-Cortex.ps1 -BridgeOnly
    # Skip container rebuild, start from existing image
#>

[CmdletBinding()]
param(
    [string]$HubToken = "dev-token-change-me",
    [switch]$BridgeOnly,
    [switch]$BumpVersion
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

# ── Sync hub token ────────────────────────────────────────────
$env:CORTEX_HUB_TOKEN = $HubToken
Write-Host "Hub token set for both Agent and Bridge." -ForegroundColor Gray

$composeCmd = "docker compose -f docker-compose.yml"

# ── Build container image ─────────────────────────────────────
if ($BridgeOnly) {
    Write-Host ""
    Write-Host "Skipping container build (-BridgeOnly)." -ForegroundColor Yellow
} else {
    Write-Host ""
    if ($BumpVersion) {
        & (Join-Path $scriptDir "Bump-Version.ps1") | Out-Null
    }
    & (Join-Path $scriptDir "Build-AgentImage.ps1")
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Container build failed."
        exit 1
    }
}

# ── Start Agent container in background ───────────────────────
Write-Host ""
Write-Host "Starting Agent container..." -ForegroundColor Cyan
$agentJob = Start-Job -ScriptBlock {
    param($root, $token, $cmd)
    $env:CORTEX_HUB_TOKEN = $token
    Set-Location $root
    Invoke-Expression "$cmd up --force-recreate 2>&1"
} -ArgumentList $repoRoot, $HubToken, $composeCmd

# Wait for the agent to be healthy
Write-Host "Waiting for Agent to be ready..." -ForegroundColor Gray
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    try {
        $response = Invoke-RestMethod -Uri "http://127.0.0.1:5100/health" -TimeoutSec 2 -ErrorAction SilentlyContinue
        if ($response.healthy) {
            $ready = $true
            break
        }
    } catch {
        # Not ready yet
    }

    # Check if the job failed
    if ($agentJob.State -eq "Failed") {
        Write-Host ""
        Receive-Job $agentJob
        Write-Error "Agent container failed to start."
        exit 1
    }
}

if (-not $ready) {
    Write-Warning "Agent did not become healthy within 30s. Continuing anyway..."
    Write-Host "Recent agent output:" -ForegroundColor Yellow
    Receive-Job $agentJob -Keep | Select-Object -Last 10 | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
} else {
    Write-Host "Agent is healthy." -ForegroundColor Green
}

# ── Start Bridge in foreground ────────────────────────────────
Write-Host ""
Write-Host "Starting Bridge..." -ForegroundColor Cyan
Write-Host "Open http://127.0.0.1:5080 in your browser." -ForegroundColor Green
Write-Host "Press Ctrl+C to stop." -ForegroundColor Gray
Write-Host ""

try {
    dotnet run --project (Join-Path $repoRoot "src\Cortex.Contained.Bridge")
} finally {
    # ── Cleanup ───────────────────────────────────────────────
    Write-Host ""
    Write-Host "Stopping Agent container..." -ForegroundColor Yellow
    Set-Location $repoRoot
    Invoke-Expression "$composeCmd down" 2>&1 | Out-Null
    Stop-Job $agentJob -ErrorAction SilentlyContinue
    Remove-Job $agentJob -Force -ErrorAction SilentlyContinue
    Write-Host "Stopped." -ForegroundColor Green
}
