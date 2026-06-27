<#
.SYNOPSIS
    Builds the danish-tts sidecar Docker image with SemVer tagging.

.DESCRIPTION
    Reads the version from lib/danish-tts/version.json and builds the
    danish-tts Docker image with two tags: danish-tts:<version> +
    danish-tts:latest. Prunes old versioned images, keeping the latest 2.
    Mirrors the Build-VoiceIdImage.ps1 pattern for the voice-id sidecar.

.PARAMETER NoPrune
    Skip the old image cleanup step.

.EXAMPLE
    .\Build-DanishTtsImage.ps1
    .\Build-DanishTtsImage.ps1 -NoPrune
#>
param(
    [switch]$NoPrune
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$danishTtsDir = Join-Path $repoRoot "lib\danish-tts"

if (-not (Test-Path $danishTtsDir)) {
    Write-Error "danish-tts directory not found at $danishTtsDir."
    exit 1
}

# Read version from the danish-tts sidecar's own version.json
$danishTtsVersionFile = Join-Path $danishTtsDir "version.json"
if (-not (Test-Path $danishTtsVersionFile)) {
    Write-Error "danish-tts version.json not found at $danishTtsVersionFile."
    exit 1
}
$ver = Get-Content $danishTtsVersionFile -Raw | ConvertFrom-Json
$version = "$($ver.major).$($ver.minor).$($ver.patch)"

Write-Host "Building danish-tts:$version" -ForegroundColor Cyan

# Build with version tag + latest, from the sidecar's Dockerfile
docker build `
    -t "danish-tts:$version" `
    -t "danish-tts:latest" `
    -f (Join-Path $danishTtsDir "Dockerfile") `
    $danishTtsDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker build failed"
    exit 1
}

Write-Host ""
Write-Host "Built danish-tts:$version" -ForegroundColor Green
Write-Host "Tagged danish-tts:latest" -ForegroundColor Green

# Cleanup: keep latest 2 versioned images
if (-not $NoPrune) {
    Write-Host ""
    Write-Host "Pruning old danish-tts images..." -ForegroundColor Yellow

    $allTags = docker images danish-tts --format "{{.Tag}}" |
        Where-Object { $_ -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [int]($_ -split '\.')[-1] } -Descending

    $toRemove = $allTags | Select-Object -Skip 2
    foreach ($tag in $toRemove) {
        Write-Host "  Removing danish-tts:$tag"
        docker rmi "danish-tts:$tag" 2>$null
    }

    docker image prune -f 2>$null | Out-Null

    Write-Host "Cleanup complete." -ForegroundColor Green
}

Write-Host ""
Write-Host "Version: $version" -ForegroundColor Cyan
