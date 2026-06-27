<#
.SYNOPSIS
    Builds the Agent Host Docker image with SemVer tagging.

.DESCRIPTION
    Reads version from version.json and builds the Docker image with
    two tags (versioned + latest). Prunes old images keeping latest 2.
    Uses Docker Desktop.

.PARAMETER NoPrune
    Skip the old image cleanup step.

.PARAMETER BumpVersion
    Increment patch in version.json before building.

.EXAMPLE
    .\Build-AgentImage.ps1
    .\Build-AgentImage.ps1 -NoPrune -BumpVersion
#>
param(
    [switch]$NoPrune,
    [switch]$BumpVersion
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

# Bump or read version
if ($BumpVersion) {
    $version = & (Join-Path $scriptDir "Bump-Version.ps1")
} else {
    $version = & (Join-Path $scriptDir "Get-Version.ps1")
}

Write-Host "Building cortex-agent:$version" -ForegroundColor Cyan

# Build with version tag + latest
$dockerfilePath = Join-Path $repoRoot "src\Cortex.Contained.Agent.Host\Dockerfile"
docker build `
    -t "cortex-agent:$version" `
    -t "cortex-agent:latest" `
    --target common `
    -f $dockerfilePath `
    $repoRoot

if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker build failed"
    exit 1
}

Write-Host ""
Write-Host "Built cortex-agent:$version" -ForegroundColor Green
Write-Host "Tagged cortex-agent:latest" -ForegroundColor Green

# Cleanup: keep latest 2 versioned images
if (-not $NoPrune) {
    Write-Host ""
    Write-Host "Pruning old images..." -ForegroundColor Yellow

    $allTags = docker images cortex-agent --format "{{.Tag}}" |
        Where-Object { $_ -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [int]($_ -split '\.')[-1] } -Descending

    $toRemove = $allTags | Select-Object -Skip 2
    foreach ($tag in $toRemove) {
        Write-Host "  Removing cortex-agent:$tag"
        docker rmi "cortex-agent:$tag" 2>$null
    }

    docker image prune -f 2>$null
    docker builder prune -f --filter "until=24h" 2>$null

    Write-Host "Cleanup complete." -ForegroundColor Green
}

Write-Host ""
Write-Host "Version: $version" -ForegroundColor Cyan
