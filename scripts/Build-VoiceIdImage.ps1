<#
.SYNOPSIS
    Builds the voice-id sidecar Docker image with SemVer tagging.

.DESCRIPTION
    Reads the version from lib/voice-id/version.json (the submodule's own
    versioning) and builds the voice-id Docker image with two tags:
    voice-id:<version> + voice-id:latest. Prunes old versioned images,
    keeping the latest 2. Mirrors the Build-AgentImage.ps1 pattern for the
    Cortex agent image.

.PARAMETER NoPrune
    Skip the old image cleanup step.

.EXAMPLE
    .\Build-VoiceIdImage.ps1
    .\Build-VoiceIdImage.ps1 -NoPrune
#>
param(
    [switch]$NoPrune
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$voiceIdDir = Join-Path $repoRoot "lib\voice-id"

if (-not (Test-Path $voiceIdDir)) {
    Write-Error "voice-id submodule not found at $voiceIdDir. Run: git submodule update --init --recursive"
    exit 1
}

# Read version from the voice-id submodule's own version.json
$voiceIdVersionFile = Join-Path $voiceIdDir "version.json"
if (-not (Test-Path $voiceIdVersionFile)) {
    Write-Error "voice-id version.json not found at $voiceIdVersionFile. Submodule may be out of date."
    exit 1
}
$ver = Get-Content $voiceIdVersionFile -Raw | ConvertFrom-Json
$version = "$($ver.major).$($ver.minor).$($ver.patch)"

Write-Host "Building voice-id:$version" -ForegroundColor Cyan

# Build with version tag + latest, from the submodule's Dockerfile
docker build `
    -t "voice-id:$version" `
    -t "voice-id:latest" `
    -f (Join-Path $voiceIdDir "Dockerfile") `
    $voiceIdDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker build failed"
    exit 1
}

Write-Host ""
Write-Host "Built voice-id:$version" -ForegroundColor Green
Write-Host "Tagged voice-id:latest" -ForegroundColor Green

# Cleanup: keep latest 2 versioned images
if (-not $NoPrune) {
    Write-Host ""
    Write-Host "Pruning old voice-id images..." -ForegroundColor Yellow

    $allTags = docker images voice-id --format "{{.Tag}}" |
        Where-Object { $_ -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [int]($_ -split '\.')[-1] } -Descending

    $toRemove = $allTags | Select-Object -Skip 2
    foreach ($tag in $toRemove) {
        Write-Host "  Removing voice-id:$tag"
        docker rmi "voice-id:$tag" 2>$null
    }

    # Also remove any stray :local tag from the pre-versioned era
    $localTag = docker images voice-id --format "{{.Tag}}" | Where-Object { $_ -eq "local" }
    if ($localTag) {
        Write-Host "  Removing voice-id:local (pre-versioned tag)"
        docker rmi "voice-id:local" 2>$null
    }

    docker image prune -f 2>$null | Out-Null

    Write-Host "Cleanup complete." -ForegroundColor Green
}

Write-Host ""
Write-Host "Version: $version" -ForegroundColor Cyan
