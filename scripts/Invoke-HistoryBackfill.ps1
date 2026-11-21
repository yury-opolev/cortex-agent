<#
.SYNOPSIS
    Runs the explicit history event backfill (increment 2, D2) on a tenant's agent.

.DESCRIPTION
    Writes legacy_import events for every stored message that predates the event log. The agent
    runs it inside a maintenance window (recall and purges wait while it runs). Idempotent: a
    second run reports 0 inserted. Never resurrects deleted history.

.EXAMPLE
    .\scripts\Invoke-HistoryBackfill.ps1
    .\scripts\Invoke-HistoryBackfill.ps1 -TenantId default -BridgeUrl http://localhost:5080
#>
[CmdletBinding()]
param(
    [string]$TenantId = 'default',
    [string]$BridgeUrl = 'http://localhost:5080',
    [securestring]$Password
)

$ErrorActionPreference = 'Stop'

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt 'Bridge password'
}

$plain = [System.Net.NetworkCredential]::new('', $Password).Password
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/auth/login" -WebSession $session `
    -ContentType 'application/json' -Body (@{ password = $plain } | ConvertTo-Json) | Out-Null
$plain = $null

$tenant = [uri]::EscapeDataString($TenantId)
$report = Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/tenants/$tenant/history/event-backfill" `
    -WebSession $session -TimeoutSec 3600

Write-Host ("Scanned {0} messages, wrote {1} legacy_import events in {2} ms." -f $report.scanned, $report.inserted, $report.elapsedMs)
