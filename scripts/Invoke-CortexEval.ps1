<#
.SYNOPSIS
    Runs an evaluation command (history-eval, memory-eval, eval-corpus, judge-calibration) inside the agent process.

.DESCRIPTION
    Only the agent process holds the storage key, so on an encrypted install the evaluation commands run
    there as jobs (encrypted-workspace mode): corpus copies are encrypted with the evaluation key, datasets
    and seeds stay sealed, reports hold ids and numbers only. The same path works on an unencrypted install.
    The script logs in to the Bridge, starts the job (the Bridge checks the password again), polls the job
    and prints its output. --dir, --source, --labels, --embedder and --reranker are refused for jobs: they
    always use the configured workspace, stores and endpoints.

.EXAMPLE
    .\scripts\Invoke-CortexEval.ps1 -Tool history-eval -- ablate --split dev
    .\scripts\Invoke-CortexEval.ps1 -Tool memory-eval -- holdout
    .\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -PromptPassphrase -- export --version v1
    .\scripts\Invoke-CortexEval.ps1 -Tool judge-calibration -- calibrate --models claude-opus-5.5,grok-4.7 --rubrics default,v2
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('history-eval', 'memory-eval', 'eval-corpus', 'judge-calibration')]
    [string]$Tool,

    [Parameter(ValueFromRemainingArguments)]
    [string[]]$Arguments,

    [string]$TenantId = 'default',
    [string]$BridgeUrl = 'http://localhost:5080',
    [securestring]$Password,

    # Asks for the package passphrase (eval-corpus export and import); it is sent once, held by the job only.
    [switch]$PromptPassphrase,

    [int]$PollSeconds = 5
)

$ErrorActionPreference = 'Stop'

if (-not $Arguments -or $Arguments.Count -eq 0) {
    throw 'Pass the command after the tool, e.g. -Tool history-eval -- ablate --split dev'
}

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt 'Bridge password'
}

$plain = [System.Net.NetworkCredential]::new('', $Password).Password
$passphrase = $null
if ($PromptPassphrase) {
    $secure = Read-Host -AsSecureString -Prompt 'Package passphrase (at least 12 characters)'
    $passphrase = [System.Net.NetworkCredential]::new('', $secure).Password
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/auth/login" -WebSession $session `
    -ContentType 'application/json' -Body (@{ password = $plain } | ConvertTo-Json) | Out-Null

$tenant = [uri]::EscapeDataString($TenantId)
$body = @{ password = $plain; tool = $Tool; args = @($Arguments); passphrase = $passphrase } | ConvertTo-Json
$plain = $null
$passphrase = $null
try {
    $job = Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/tenants/$tenant/eval/jobs" -WebSession $session `
        -ContentType 'application/json' -Body $body
}
catch {
    throw "The job was not started: $($_.ErrorDetails.Message)"
}
finally {
    $body = $null
}

Write-Host "Job $($job.id): $Tool $($job.command) (encrypted workspace: $($job.encrypted))"
$printed = 0
while ($true) {
    Start-Sleep -Seconds $PollSeconds
    $job = Invoke-RestMethod -Method Get -Uri "$BridgeUrl/api/tenants/$tenant/eval/jobs/$($job.id)" -WebSession $session
    $output = [string]$job.output
    if ($output.Length -gt $printed) {
        Write-Host -NoNewline $output.Substring($printed)
        $printed = $output.Length
    }

    if ($job.state -ne 'running') {
        break
    }
}

Write-Host "State: $($job.state), exit code: $($job.exitCode)"
if ($job.state -ne 'succeeded') {
    exit 1
}
