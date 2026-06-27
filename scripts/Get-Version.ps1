<#
.SYNOPSIS
    Reads the current version from version.json without incrementing.

.EXAMPLE
    $version = .\Get-Version.ps1
    # Returns "0.2.118"
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSCommandPath -Parent) -Parent
$versionFile = Join-Path $repoRoot "version.json"

if (-not (Test-Path $versionFile)) {
    throw "version.json not found at $versionFile"
}

$ver = Get-Content $versionFile -Raw | ConvertFrom-Json
return "$($ver.major).$($ver.minor).$($ver.patch)"
