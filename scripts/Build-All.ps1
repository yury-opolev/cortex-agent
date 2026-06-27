<#
.SYNOPSIS
    Bumps version, builds Docker images, and packages the MSIX launcher.

.DESCRIPTION
    Single entry point that:
    1. Increments patch in version.json
    2. Builds the Agent Host Docker image (cortex-agent:X.Y.Z + latest)
    3. Builds the voice-id sidecar Docker image (voice-id:X.Y.Z + latest)
    4. Builds the Launcher + Bridge MSIX package (signed)

.PARAMETER SkipDocker
    Skip the Docker image builds (both cortex-agent and voice-id).

.PARAMETER SkipMsix
    Skip the MSIX packaging step.

.PARAMETER NoPrune
    Skip old Docker image cleanup.

.PARAMETER CertThumbprint
    Optional exact thumbprint for MSIX signing. Overrides subject lookup. Defaults
    to the CORTEX_SIGNING_THUMBPRINT environment variable. When empty, Build-Launcher
    resolves the signing cert by its Subject CN. See docs/msix-signing.md.

.PARAMETER CertSubject
    Optional signing-cert Subject (CN) override, passed through to Build-Launcher.
    Defaults to the CORTEX_SIGNING_SUBJECT environment variable.

.EXAMPLE
    .\Build-All.ps1              # Build everything
    .\Build-All.ps1 -SkipMsix    # Docker only
    .\Build-All.ps1 -SkipDocker  # MSIX only
#>
param(
    [switch]$SkipDocker,
    [switch]$SkipMsix,
    [switch]$NoPrune,
    [string]$CertThumbprint = $env:CORTEX_SIGNING_THUMBPRINT,
    [string]$CertSubject = $env:CORTEX_SIGNING_SUBJECT
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. Bump version
Write-Host "=== Bumping version ===" -ForegroundColor Cyan
$version = & (Join-Path $scriptDir "Bump-Version.ps1")
Write-Host ""

# 2. Build Docker images (cortex-agent + voice-id)
if (-not $SkipDocker) {
    Write-Host "=== Building cortex-agent Docker image ===" -ForegroundColor Cyan
    $buildArgs = @{}
    if ($NoPrune) { $buildArgs["NoPrune"] = $true }
    & (Join-Path $scriptDir "Build-AgentImage.ps1") @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "cortex-agent build failed" }
    Write-Host ""

    Write-Host "=== Building voice-id Docker image ===" -ForegroundColor Cyan
    & (Join-Path $scriptDir "Build-VoiceIdImage.ps1") @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "voice-id build failed" }
    Write-Host ""

    # NOTE: The unified TTS sidecar (uni-voices) is NOT built here. Its image is
    # large (all weights baked in) and is published to GHCR / pulled via
    # `docker compose --profile tts build uni-voices`. See lib/uni-voices.
} else {
    Write-Host "Skipping Docker builds (-SkipDocker)." -ForegroundColor Yellow
    Write-Host ""
}

# 3. Build MSIX
if (-not $SkipMsix) {
    Write-Host "=== Building MSIX ===" -ForegroundColor Cyan
    # Pass signing overrides through only when set; otherwise Build-Launcher
    # resolves the cert by its (stable) Subject CN.
    $launcherArgs = @{}
    if ($CertThumbprint) { $launcherArgs["CertThumbprint"] = $CertThumbprint }
    if ($CertSubject) { $launcherArgs["CertSubject"] = $CertSubject }
    & (Join-Path $scriptDir "Build-Launcher.ps1") @launcherArgs
    if ($LASTEXITCODE -ne 0) { throw "MSIX build failed" }
    Write-Host ""
} else {
    Write-Host "Skipping MSIX build (-SkipMsix)." -ForegroundColor Yellow
    Write-Host ""
}

Write-Host "=== All builds complete: v$version ===" -ForegroundColor Green
