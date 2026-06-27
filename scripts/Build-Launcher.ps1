<#
.SYNOPSIS
    Builds the Cortex Launcher and Bridge, packages as signed MSIX.

.DESCRIPTION
    Publishes the Launcher and Bridge as self-contained executables,
    combines them into a single output directory, packages as MSIX,
    and signs with the local certificate.
    Does NOT bump the version — use Build-All.ps1 or Bump-Version.ps1 for that.

.PARAMETER OutputDir
    Output directory for the packaged application. Defaults to 'artifacts/launcher'.

.PARAMETER Configuration
    Build configuration. Defaults to 'Release'.

.PARAMETER SkipMsix
    Skip MSIX packaging and signing (just publish binaries).

.PARAMETER CertThumbprint
    Optional exact thumbprint to sign with. Overrides subject lookup. Defaults to
    the CORTEX_SIGNING_THUMBPRINT environment variable (empty = use subject lookup).

.PARAMETER CertSubject
    Subject (CN) of the signing certificate to look up in the local certificate
    store when no thumbprint is given. Defaults to the CORTEX_SIGNING_SUBJECT
    environment variable, or the Cortex package Publisher identity. This is the
    stable identity that does not change across certificate renewals — it must
    match Package.appxmanifest's <Identity Publisher="..."> value.
#>
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\artifacts\launcher"),
    [string]$Configuration = "Release",
    [switch]$SkipMsix,
    [switch]$BumpVersion,
    [string]$CertThumbprint = $env:CORTEX_SIGNING_THUMBPRINT,
    [string]$CertSubject = $(if ($env:CORTEX_SIGNING_SUBJECT) { $env:CORTEX_SIGNING_SUBJECT } else { "CN=B4C4AA96-F301-4E3B-AA5D-25A99E1D356D" })
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$launcherProject = "$repoRoot\src\Cortex.Contained.Launcher"

# Bump or read version
if ($BumpVersion) {
    $version = & (Join-Path $PSScriptRoot "Bump-Version.ps1")
} else {
    $version = & (Join-Path $PSScriptRoot "Get-Version.ps1")
}

Write-Host "Building Cortex Launcher $version ($Configuration)..." -ForegroundColor Cyan

# Clean output
if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}

# Publish Launcher
Write-Host "`nPublishing Launcher..." -ForegroundColor Yellow
dotnet publish "$launcherProject\Cortex.Contained.Launcher.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -o "$OutputDir"

if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed" }

# Publish Bridge into a subdirectory
Write-Host "`nPublishing Bridge..." -ForegroundColor Yellow
dotnet publish "$repoRoot\src\Cortex.Contained.Bridge\Cortex.Contained.Bridge.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -o "$OutputDir\Bridge"

if ($LASTEXITCODE -ne 0) { throw "Bridge publish failed" }

# Publish coda (the coding engine) self-contained into the Bridge payload so the
# MSIX ships coda.exe alongside the Bridge (resolved at runtime via
# CodaOptions.ResolveDefaultBinaryPath -> <BaseDir>\coda\coda.exe).
Write-Host "`nPublishing coda (coding engine)..." -ForegroundColor Yellow
$codaProject = "$repoRoot\lib\coda-cli\src\Coda.Tui\Coda.Tui.csproj"
if (Test-Path $codaProject) {
    $codaOut = "$OutputDir\Bridge\coda"
    dotnet publish "$codaProject" `
        -c $Configuration `
        -r win-x64 `
        --self-contained `
        -o "$codaOut"
    if ($LASTEXITCODE -ne 0) { throw "coda publish failed" }

    # coda ships as coda.exe — rename the published Coda.Tui.exe (coda's own
    # publish.ps1 does the same; AssemblyName is intentionally NOT overridden
    # because it would propagate to referenced assemblies).
    $codaSrc = Join-Path $codaOut "Coda.Tui.exe"
    $codaDst = Join-Path $codaOut "coda.exe"
    if (Test-Path $codaSrc) {
        Move-Item -Force $codaSrc $codaDst
        Write-Host "Bundled coda.exe -> $codaDst" -ForegroundColor Gray
    } else {
        throw "Expected published Coda.Tui.exe not found at $codaSrc"
    }
} else {
    Write-Warning "coda project not found at $codaProject (submodule missing?); MSIX will rely on coda being on PATH."
}

# Copy docker-compose.yml
Copy-Item "$repoRoot\docker-compose.yml" "$OutputDir\docker-compose.yml" -Force

# Copy version.json for runtime version display
Copy-Item "$repoRoot\version.json" "$OutputDir\version.json" -Force

Write-Host "`nBuild complete: Cortex v$version" -ForegroundColor Green
Write-Host "Output: $OutputDir" -ForegroundColor Green

if ($SkipMsix) {
    Write-Host "`nSkipping MSIX packaging (-SkipMsix)." -ForegroundColor Yellow
    return
}

# --- MSIX Packaging ---
Write-Host "`nPackaging MSIX..." -ForegroundColor Cyan

# Copy manifest and assets into the output directory
Copy-Item "$launcherProject\Package.appxmanifest" "$OutputDir\AppxManifest.xml" -Force
if (Test-Path "$launcherProject\Assets") {
    Copy-Item "$launcherProject\Assets" "$OutputDir\Assets" -Recurse -Force
}

# Patch Identity version — MSIX requires 4-part version: Major.Minor.Patch.0
$msixVersion = "$version.0"
$manifest = Get-Content "$OutputDir\AppxManifest.xml" -Raw
$manifest = $manifest -replace '(<Identity\s[^>]*?)Version="[^"]*"', "`$1Version=`"$msixVersion`""
Set-Content "$OutputDir\AppxManifest.xml" $manifest -NoNewline
Write-Host "Manifest version: $msixVersion" -ForegroundColor Gray

# Locate Windows SDK tools
$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
if (-not (Test-Path $sdkRoot)) {
    throw "Windows 10 SDK not found at $sdkRoot. Install Windows SDK."
}

$sdkVersion = Get-ChildItem $sdkRoot -Directory | Where-Object { $_.Name -match '^\d+\.' } |
    Sort-Object Name -Descending | Select-Object -First 1

$makeAppx = Join-Path $sdkVersion.FullName "x64\makeappx.exe"
$signTool = Join-Path $sdkVersion.FullName "x64\signtool.exe"

if (-not (Test-Path $makeAppx)) { throw "MakeAppx.exe not found at $makeAppx" }
if (-not (Test-Path $signTool)) { throw "SignTool.exe not found at $signTool" }

Write-Host "Using SDK: $($sdkVersion.Name)" -ForegroundColor Gray

# Build MSIX package
$msixPath = Join-Path $repoRoot "artifacts\CortexLauncher-$version.msix"

Write-Host "Creating MSIX package..." -ForegroundColor Yellow
& $makeAppx pack /d "$OutputDir" /p "$msixPath" /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx pack failed" }

# Resolve the signing certificate.
# Priority: explicit/env thumbprint (exact pin) > lookup by Subject CN in the
# local certificate store. Subject lookup survives certificate renewal as long
# as the renewed cert keeps the same CN — which it must, because that CN is the
# MSIX Publisher identity (see Package.appxmanifest). See docs/msix-signing.md.
$signingThumbprint = $CertThumbprint
if ($signingThumbprint) {
    Write-Host "Signing cert: explicit thumbprint $signingThumbprint" -ForegroundColor Gray
} else {
    $now = Get-Date
    $signingCert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey -and $_.NotAfter -gt $now } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if (-not $signingCert) {
        throw @"
No valid code-signing certificate found for subject '$CertSubject'.
Looked in Cert:\CurrentUser\My and Cert:\LocalMachine\My for a cert with a
private key and a future expiry.

Resolve one of:
  - Import/restore the Cortex signing certificate (its subject must stay '$CertSubject').
  - Pass -CertThumbprint <thumbprint> or set `$env:CORTEX_SIGNING_THUMBPRINT.
  - Set `$env:CORTEX_SIGNING_SUBJECT if the signing subject legitimately changed.

See docs/msix-signing.md for the certificate renewal procedure.
"@
    }

    $signingThumbprint = $signingCert.Thumbprint
    Write-Host ("Signing cert: {0} [{1}], expires {2}" -f `
        $signingCert.Subject, $signingThumbprint, $signingCert.NotAfter.ToString('yyyy-MM-dd')) -ForegroundColor Gray
}

# Sign MSIX with the resolved certificate
Write-Host "Signing MSIX..." -ForegroundColor Yellow
& $signTool sign /sha1 "$signingThumbprint" /fd SHA256 /td SHA256 "$msixPath"
if ($LASTEXITCODE -ne 0) { throw "SignTool sign failed" }

Write-Host "`nMSIX ready: $msixPath" -ForegroundColor Green
Write-Host "Signed with thumbprint: $signingThumbprint" -ForegroundColor Green
