<#
.SYNOPSIS
    Builds the cortex-reranker sidecar image (llama.cpp server + baked reranker model, all pinned).

.DESCRIPTION
    Reads the version from docker/reranker/version.json and builds docker/reranker/Dockerfile with two
    tags: cortex-reranker:<version> + cortex-reranker:latest. The Dockerfile pins the llama.cpp server
    image by digest and the GGUF model by sha256, so a rebuild of the same version yields the same
    content (and, with the build cache, the same image id). Prunes old versioned images, keeping 2.

    Building it once opts this machine in: from then on Build-All rebuilds it on every Self-Update and
    the manifest pins its image id. The Bridge only runs it when Reranker:Enabled is true.

.PARAMETER Variant
    cuda (GPU, falls back to CPU without one) or cpu (small CPU-only image). Default: cuda when
    nvidia-smi is available, else cpu. The Gate R latency budget needs the GPU (docs/reranker.md).

.PARAMETER NoPrune
    Skip the old image cleanup step.

.EXAMPLE
    .\Build-RerankerImage.ps1
    .\Build-RerankerImage.ps1 -Variant cpu -NoPrune
#>
param(
    [ValidateSet('', 'cuda', 'cpu')]
    [string]$Variant = '',
    [switch]$NoPrune
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$contextDir = Join-Path $repoRoot "docker\reranker"

if (-not $Variant) {
    $Variant = if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { 'cuda' } else { 'cpu' }
}

$ver = Get-Content (Join-Path $contextDir "version.json") -Raw | ConvertFrom-Json
$version = "$($ver.major).$($ver.minor).$($ver.patch)"

Write-Host "Building cortex-reranker:$version ($Variant)" -ForegroundColor Cyan

docker build `
    --build-arg "VARIANT=$Variant" `
    --label "cortex.reranker.variant=$Variant" `
    -t "cortex-reranker:$version" `
    -t "cortex-reranker:latest" `
    $contextDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker build failed"
    exit 1
}

$imageId = docker image inspect cortex-reranker:latest --format '{{.Id}}'
Write-Host ""
Write-Host "Built cortex-reranker:$version ($Variant), image $imageId" -ForegroundColor Green

if (-not $NoPrune) {
    Write-Host ""
    Write-Host "Pruning old cortex-reranker images..." -ForegroundColor Yellow

    $allTags = docker images cortex-reranker --format "{{.Tag}}" |
        Where-Object { $_ -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_ } -Descending

    foreach ($tag in ($allTags | Select-Object -Skip 2)) {
        Write-Host "  Removing cortex-reranker:$tag"
        docker rmi "cortex-reranker:$tag" 2>$null
    }

    docker image prune -f 2>$null | Out-Null
    Write-Host "Cleanup complete." -ForegroundColor Green
}

# Best-effort cleanup commands may exit non-zero; Build-All checks this script's exit code.
$global:LASTEXITCODE = 0

Write-Host ""
Write-Host "Version: $version" -ForegroundColor Cyan
