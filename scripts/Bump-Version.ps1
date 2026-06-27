<#
.SYNOPSIS
    Increments the patch number in version.json and returns the new version string.

.EXAMPLE
    $version = .\Bump-Version.ps1
    # Returns "0.2.118" (or whatever the next patch is)
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSCommandPath -Parent) -Parent
$versionFile = Join-Path $repoRoot "version.json"

if (-not (Test-Path $versionFile)) {
    throw "version.json not found at $versionFile"
}

$ver = Get-Content $versionFile -Raw | ConvertFrom-Json
$ver.patch = $ver.patch + 1
$ver | ConvertTo-Json | Set-Content $versionFile

$version = "$($ver.major).$($ver.minor).$($ver.patch)"
Write-Host "Version: $version" -ForegroundColor Cyan
return $version
