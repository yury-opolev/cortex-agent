#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs Cortex Bridge as a Windows Service.

.DESCRIPTION
    Publishes the Cortex.Contained.Bridge project and registers it as a Windows Service
    that starts automatically on boot with automatic restart on failure.

.PARAMETER InstallPath
    Directory where the published binaries will be placed.
    Defaults to C:\Cortex\Bridge.

.PARAMETER ServiceName
    The Windows Service name. Defaults to "CortexBridge".

.PARAMETER DisplayName
    The Windows Service display name. Defaults to "Cortex AI Bridge".

.PARAMETER HubToken
    The authentication token for connecting to the Agent Hub.
    If not provided, the script will prompt for it.

.EXAMPLE
    .\Install-CortexBridge.ps1 -HubToken "my-secret-token"

.EXAMPLE
    .\Install-CortexBridge.ps1 -InstallPath "D:\Services\Cortex" -ServiceName "CortexBridgeDev"
#>

[CmdletBinding()]
param(
    [string]$InstallPath = "C:\Cortex\Bridge",
    [string]$DataPath = "C:\ProgramData\Cortex",
    [string]$ServiceName = "CortexBridge",
    [string]$DisplayName = "Cortex AI Bridge",
    [string]$HubToken
)

$ErrorActionPreference = "Stop"

# ── Validate prerequisites ────────────────────────────────────
Write-Host "Checking prerequisites..." -ForegroundColor Cyan

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Error ".NET SDK is not installed or not in PATH."
    exit 1
}

$sdkVersion = dotnet --version
Write-Host "  .NET SDK: $sdkVersion" -ForegroundColor Gray

# ── Check for existing service ─────────────────────────────────
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warning "Service '$ServiceName' already exists (Status: $($existing.Status))."
    $response = Read-Host "Do you want to stop, remove, and reinstall? (y/N)"
    if ($response -ne 'y') {
        Write-Host "Aborted." -ForegroundColor Yellow
        exit 0
    }

    if ($existing.Status -eq 'Running') {
        Write-Host "Stopping service..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }

    Write-Host "Removing existing service..." -ForegroundColor Yellow
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

# ── Prompt for Hub Token if not provided ───────────────────────
if (-not $HubToken) {
    $secureToken = Read-Host "Enter Hub Token for Agent Hub authentication" -AsSecureString
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    $HubToken = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

if ([string]::IsNullOrWhiteSpace($HubToken)) {
    Write-Error "Hub token is required."
    exit 1
}

# ── Locate project ────────────────────────────────────────────
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectPath = Join-Path $repoRoot "src\Cortex.Contained.Bridge\Cortex.Contained.Bridge.csproj"

if (-not (Test-Path $projectPath)) {
    Write-Error "Project not found at $projectPath. Run this script from the repository's scripts/ directory."
    exit 1
}

# ── Publish ────────────────────────────────────────────────────
Write-Host ""
Write-Host "Publishing Cortex.Contained.Bridge to $InstallPath..." -ForegroundColor Cyan

if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}

dotnet publish $projectPath `
    --configuration Release `
    --output $InstallPath `
    --self-contained false `
    --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit 1
}

Write-Host "  Published successfully." -ForegroundColor Green

# ── Create data directories with restrictive ACLs ─────────────
Write-Host ""
Write-Host "Setting up data directories at $DataPath..." -ForegroundColor Cyan

$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$adminsSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
$adminsAccount = $adminsSid.Translate([System.Security.Principal.NTAccount]).Value

# Directories requiring owner-only access
$ownerOnlyDirs = @(
    (Join-Path $DataPath "secrets")
)

# Directories requiring owner + admin access
$ownerAndAdminDirs = @(
    (Join-Path $DataPath "logs")
)

function Set-RestrictiveAcl {
    param(
        [string]$Path,
        [string[]]$AllowedAccounts,
        [string]$Rights = "FullControl"
    )

    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }

    $acl = Get-Acl -Path $Path

    # Remove inheritance and clear existing rules
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($existingRule in $acl.Access) {
        $acl.RemoveAccessRule($existingRule) | Out-Null
    }

    # Add explicit rules for each allowed account
    foreach ($account in $AllowedAccounts) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $account,
            $Rights,
            "ContainerInherit,ObjectInherit",
            "None",
            "Allow")
        $acl.SetAccessRule($rule)
    }

    Set-Acl -Path $Path -AclObject $acl
    Write-Host "  ACL set on $Path -> $($AllowedAccounts -join ', ')" -ForegroundColor Gray
}

# Create and secure owner-only directories
foreach ($dir in $ownerOnlyDirs) {
    Set-RestrictiveAcl -Path $dir -AllowedAccounts @($currentUser)
}

# Create and secure owner + admin directories
foreach ($dir in $ownerAndAdminDirs) {
    Set-RestrictiveAcl -Path $dir -AllowedAccounts @($currentUser, $adminsAccount)
}

# Secure the root data directory too (owner + admin)
Set-RestrictiveAcl -Path $DataPath -AllowedAccounts @($currentUser, $adminsAccount)

Write-Host "  Data directories secured." -ForegroundColor Green

# ── Write environment config ──────────────────────────────────
$envFile = Join-Path $InstallPath "appsettings.Service.json"
$envConfig = @{
    CORTEX_HUB_TOKEN = $HubToken
} | ConvertTo-Json -Depth 3

Set-Content -Path $envFile -Value $envConfig -Encoding UTF8
Write-Host "  Configuration written to $envFile" -ForegroundColor Gray

# ── Create Windows Service ────────────────────────────────────
Write-Host ""
Write-Host "Creating Windows Service '$ServiceName'..." -ForegroundColor Cyan

$exePath = Join-Path $InstallPath "Cortex.Contained.Bridge.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found at $exePath. Publish may have failed."
    exit 1
}

# Create the service
sc.exe create $ServiceName `
    binPath= "`"$exePath`"" `
    start= auto `
    DisplayName= "`"$DisplayName`""

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create service."
    exit 1
}

# Set description
sc.exe description $ServiceName "Cortex AI personal assistant - Bridge service connecting channels to the agent container." | Out-Null

# Set recovery options: restart after 5s, 30s, 60s
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

Write-Host "  Service created with auto-start and failure recovery." -ForegroundColor Green

# ── Set environment variable for the service ──────────────────
# The service will read from appsettings.Service.json via ASPNETCORE_ENVIRONMENT
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
if (Test-Path $regPath) {
    $envBlock = @(
        "ASPNETCORE_ENVIRONMENT=Service"
    )
    # Note: multi-string environment variables for services are set via registry
    $envRegPath = "$regPath\Environment"
    # Sadly sc.exe doesn't support env vars directly; we use the config file approach instead
}

# ── Start the service ─────────────────────────────────────────
Write-Host ""
$startNow = Read-Host "Start the service now? (Y/n)"
if ($startNow -ne 'n') {
    Write-Host "Starting service..." -ForegroundColor Cyan
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 2

    $svc = Get-Service -Name $ServiceName
    if ($svc.Status -eq 'Running') {
        Write-Host "  Service is running." -ForegroundColor Green
    } else {
        Write-Warning "  Service status: $($svc.Status). Check Event Viewer for details."
    }
}

# ── Summary ────────────────────────────────────────────────────
Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host "  Service Name:  $ServiceName" -ForegroundColor Gray
Write-Host "  Install Path:  $InstallPath" -ForegroundColor Gray
Write-Host "  Data Path:     $DataPath" -ForegroundColor Gray
Write-Host "  Web UI:        http://127.0.0.1:5080" -ForegroundColor Gray
Write-Host ""
Write-Host "Useful commands:" -ForegroundColor Cyan
Write-Host "  Start-Service $ServiceName" -ForegroundColor Gray
Write-Host "  Stop-Service $ServiceName" -ForegroundColor Gray
Write-Host "  Get-Service $ServiceName" -ForegroundColor Gray
Write-Host "  Get-EventLog -LogName Application -Source $ServiceName -Newest 20" -ForegroundColor Gray
