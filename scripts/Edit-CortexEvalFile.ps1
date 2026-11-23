<#
.SYNOPSIS
    Reads or writes an owner-authored evaluation file (dataset, analysis plan, sanitiser dictionary, ...).

.DESCRIPTION
    On an encrypted install the files that carry personal text (dataset.jsonl, seeds.jsonl, dictionary.jsonl)
    are sealed with the storage key inside the agent, so they are edited through the Bridge (the password is
    checked again for every read and write). Without -Pull/-Push the file is downloaded to a private folder
    under %LOCALAPPDATA%\Cortex\eval-edit, opened in the editor, uploaded when the editor closes and the local
    copy deleted. -Pull and -Push move the file to and from an explicit path (for labelling with an assistant);
    delete that copy yourself when done. Identical content is not rewritten, so pre-registration hashes stay valid.

.EXAMPLE
    .\scripts\Edit-CortexEvalFile.ps1 -Tool history-eval -Name dataset.jsonl
    .\scripts\Edit-CortexEvalFile.ps1 -Tool memory-eval -Name seeds.jsonl -Pull -Path .\seeds.jsonl
    .\scripts\Edit-CortexEvalFile.ps1 -Tool eval-corpus -Name dictionary.jsonl -Push -Path .\dictionary.jsonl
#>
[CmdletBinding(DefaultParameterSetName = 'Edit', PositionalBinding = $false)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('history-eval', 'memory-eval', 'eval-corpus')]
    [string]$Tool,

    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(ParameterSetName = 'Pull', Mandatory)]
    [switch]$Pull,

    [Parameter(ParameterSetName = 'Push', Mandatory)]
    [switch]$Push,

    [Parameter(ParameterSetName = 'Pull', Mandatory)]
    [Parameter(ParameterSetName = 'Push', Mandatory)]
    [string]$Path,

    [string]$TenantId = 'default',
    [string]$BridgeUrl = 'http://localhost:5080',
    [securestring]$Password,
    [string]$Editor = $(if ($env:EDITOR) { $env:EDITOR } else { 'notepad.exe' })
)

$ErrorActionPreference = 'Stop'

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt 'Bridge password'
}

$plain = [System.Net.NetworkCredential]::new('', $Password).Password
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/auth/login" -WebSession $session `
    -ContentType 'application/json' -Body (@{ password = $plain } | ConvertTo-Json) | Out-Null
$tenant = [uri]::EscapeDataString($TenantId)

function Read-EvalFile {
    $file = Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/tenants/$tenant/eval/files/read" -WebSession $session `
        -ContentType 'application/json' -Body (@{ password = $plain; tool = $Tool; name = $Name } | ConvertTo-Json)
    if ($file.exists) { [string]$file.content } else { '' }
}

function Write-EvalFile([string]$content) {
    $result = Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/tenants/$tenant/eval/files/write" -WebSession $session `
        -ContentType 'application/json' -Body (@{ password = $plain; tool = $Tool; name = $Name; content = $content } | ConvertTo-Json)
    if ($result.unchanged) { Write-Host "$Name unchanged." } else { Write-Host "$Name written (sealed: $($result.sealed))." }
}

try {
    switch ($PSCmdlet.ParameterSetName) {
        'Pull' {
            [IO.File]::WriteAllText($Path, (Read-EvalFile))
            Write-Host "Wrote $Path. It holds plaintext: delete it when you are done."
        }
        'Push' {
            Write-EvalFile ([IO.File]::ReadAllText($Path))
        }
        default {
            $folder = Join-Path $env:LOCALAPPDATA 'Cortex\eval-edit'
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
            $local = Join-Path $folder ([guid]::NewGuid().ToString('N') + '-' + $Name)
            try {
                [IO.File]::WriteAllText($local, (Read-EvalFile))
                Start-Process -FilePath $Editor -ArgumentList "`"$local`"" -Wait
                Write-EvalFile ([IO.File]::ReadAllText($local))
            }
            finally {
                Remove-Item -LiteralPath $local -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
finally {
    $plain = $null
}
