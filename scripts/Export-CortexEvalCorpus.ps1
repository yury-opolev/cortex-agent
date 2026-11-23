<#
.SYNOPSIS
    Downloads a passphrase-encrypted evaluation corpus package (cortex-eval-corpus-vN.cxpkg) to a host folder.

.DESCRIPTION
    The package was written by `eval-corpus export --version vN` inside the agent (Invoke-CortexEval.ps1). It is
    encrypted with the package passphrase (PBKDF2 600k + AES-256-GCM, chunked), so the host copy is safe to keep
    until it is attached to the SafeExchange secret cortex-recovery-kit. The Bridge checks the password again and
    streams the package from the agent; nothing is decrypted on the way. Prints the SHA-256 to compare with the
    export output and with SafeExchange after the upload.

.EXAMPLE
    .\scripts\Export-CortexEvalCorpus.ps1 -Version v1
    .\scripts\Export-CortexEvalCorpus.ps1 -Version v2 -OutDirectory D:\backups\cortex
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v[1-9][0-9]*$')]
    [string]$Version,

    [string]$OutDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'cortex-eval-packages'),
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

$name = "cortex-eval-corpus-$Version.cxpkg"
# A relative folder is relative to the PowerShell location (not the process working directory); print the absolute path.
$OutDirectory = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($OutDirectory)
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
$target = Join-Path $OutDirectory $name
$tenant = [uri]::EscapeDataString($TenantId)
try {
    Invoke-WebRequest -Method Post -Uri "$BridgeUrl/api/tenants/$tenant/eval/frozen/packages/$name" -WebSession $session `
        -ContentType 'application/json' -Body (@{ password = $plain } | ConvertTo-Json) -OutFile $target
}
catch {
    Remove-Item -LiteralPath $target -ErrorAction SilentlyContinue
    throw "The package was not downloaded: $($_.ErrorDetails.Message)"
}
finally {
    $plain = $null
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLowerInvariant()
Write-Host "Saved $target ($((Get-Item -LiteralPath $target).Length) bytes)"
Write-Host "SHA-256 $hash"
Write-Host "Next: attach it to the SafeExchange secret cortex-recovery-kit under the file name $name (docs/evaluation-runbook.md), then record the upload with eval-corpus uploaded."
