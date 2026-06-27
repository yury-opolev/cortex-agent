#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the Cortex Bridge Windows Service.

.DESCRIPTION
    Stops and removes the Cortex Bridge Windows Service, and optionally
    deletes the installation directory with all published binaries and config.

.PARAMETER ServiceName
    The Windows Service name to remove. Defaults to "CortexBridge".

.PARAMETER InstallPath
    The installation directory to optionally delete.
    Defaults to C:\Cortex\Bridge.

.PARAMETER RemoveFiles
    If specified, deletes the installation directory after removing the service.

.PARAMETER Force
    Skip all confirmation prompts.

.EXAMPLE
    .\Uninstall-CortexBridge.ps1

.EXAMPLE
    .\Uninstall-CortexBridge.ps1 -RemoveFiles

.EXAMPLE
    .\Uninstall-CortexBridge.ps1 -ServiceName "CortexBridgeDev" -RemoveFiles -Force
#>

[CmdletBinding()]
param(
    [string]$ServiceName = "CortexBridge",
    [string]$InstallPath = "C:\Cortex\Bridge",
    [switch]$RemoveFiles,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# ── Check if service exists ───────────────────────────────────
Write-Host "Checking for service '$ServiceName'..." -ForegroundColor Cyan

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Warning "Service '$ServiceName' does not exist."
    if (-not $RemoveFiles) {
        Write-Host "Nothing to do." -ForegroundColor Yellow
        exit 0
    }
    Write-Host "Skipping service removal, checking for files..." -ForegroundColor Gray
} else {
    Write-Host "  Found service '$ServiceName' (Status: $($service.Status))" -ForegroundColor Gray

    # ── Confirm removal ───────────────────────────────────────
    if (-not $Force) {
        $response = Read-Host "Remove service '$ServiceName'? (y/N)"
        if ($response -ne 'y') {
            Write-Host "Aborted." -ForegroundColor Yellow
            exit 0
        }
    }

    # ── Stop the service ──────────────────────────────────────
    if ($service.Status -eq 'Running') {
        Write-Host "Stopping service..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
        $timeout = 15
        $elapsed = 0
        while ($elapsed -lt $timeout) {
            Start-Sleep -Seconds 1
            $elapsed++
            $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if ($service.Status -eq 'Stopped') {
                break
            }
        }
        if ($service.Status -ne 'Stopped') {
            Write-Warning "Service did not stop within $timeout seconds. Attempting force removal..."
        } else {
            Write-Host "  Service stopped." -ForegroundColor Green
        }
    } elseif ($service.Status -ne 'Stopped') {
        Write-Host "  Service is in state '$($service.Status)', waiting for it to settle..." -ForegroundColor Yellow
        Start-Sleep -Seconds 3
    }

    # ── Remove the service ────────────────────────────────────
    Write-Host "Removing service '$ServiceName'..." -ForegroundColor Cyan
    sc.exe delete $ServiceName | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to remove service. It may be marked for deletion — try rebooting and running again."
        exit 1
    }

    # Wait briefly for service manager to process deletion
    Start-Sleep -Seconds 2

    # Verify removal
    $check = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($check) {
        Write-Warning "Service is marked for deletion. It will be fully removed after reboot."
    } else {
        Write-Host "  Service removed." -ForegroundColor Green
    }
}

# ── Optionally remove files ──────────────────────────────────
if ($RemoveFiles) {
    if (Test-Path $InstallPath) {
        if (-not $Force) {
            Write-Host ""
            Write-Warning "This will permanently delete: $InstallPath"
            $response = Read-Host "Delete installation directory? (y/N)"
            if ($response -ne 'y') {
                Write-Host "Skipped file removal." -ForegroundColor Yellow
            } else {
                Write-Host "Removing $InstallPath..." -ForegroundColor Yellow
                Remove-Item -Path $InstallPath -Recurse -Force
                Write-Host "  Files removed." -ForegroundColor Green
            }
        } else {
            Write-Host "Removing $InstallPath..." -ForegroundColor Yellow
            Remove-Item -Path $InstallPath -Recurse -Force
            Write-Host "  Files removed." -ForegroundColor Green
        }
    } else {
        Write-Host "  Install path '$InstallPath' does not exist. Nothing to delete." -ForegroundColor Gray
    }
}

# ── Summary ───────────────────────────────────────────────────
Write-Host ""
Write-Host "Uninstall complete." -ForegroundColor Green
