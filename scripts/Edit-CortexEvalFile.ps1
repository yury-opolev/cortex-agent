<#
.SYNOPSIS
    Reads or writes an owner-authored evaluation file (dataset, analysis plan, sanitiser dictionary, ...).

.DESCRIPTION
    On an encrypted install the files that carry personal text (dataset.jsonl, seeds.jsonl, dictionary.jsonl)
    are sealed with the storage key inside the agent, so they are edited through the Bridge (the password is
    checked again for every read and write). Without -Pull/-Push the file is downloaded to a private folder
    under %LOCALAPPDATA%\Cortex\eval-edit and opened in the editor ($env:EDITOR or notepad.exe; -Editor "code --wait"
    works too). When the editor closes the file is uploaded only if it changed, and the local copy is overwritten and
    deleted. An editor that returns at once (it handed the file to a window that is already open) makes the script ask
    you to press Enter once you have saved and closed the file. If the upload fails the edits stay in the private
    folder (the script prints the path) instead of being lost.

    -Pull and -Push move the file to and from an explicit path (for labelling with an assistant; a relative path is
    relative to the current PowerShell location); delete that copy yourself when done. Identical content is not
    rewritten, so pre-registration hashes stay valid.

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

function Remove-LocalCopy([string]$file) {
    if (-not (Test-Path -LiteralPath $file)) { return }
    try {
        $length = (Get-Item -LiteralPath $file).Length
        if ($length -gt 0) {
            $junk = New-Object byte[] $length
            $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
            try { $rng.GetBytes($junk) } finally { $rng.Dispose() }
            [IO.File]::WriteAllBytes($file, $junk)
        }
    }
    catch { }
    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $file) {
        Write-Warning "Could not delete $file; it holds plaintext: close the editor and delete it."
    }
}

function Get-TextHash([string]$file) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash
}

if ($PSCmdlet.ParameterSetName -ne 'Edit') {
    $Path = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($Path)
}

$keep = $false
$local = $null
try {
    switch ($PSCmdlet.ParameterSetName) {
        'Pull' {
            [IO.File]::WriteAllText($Path, (Read-EvalFile))
            Write-Host "Wrote $Path. It holds plaintext: delete it when you are done."
        }
        'Push' {
            if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "No such file: $Path" }
            Write-Host "Reading $Path"
            Write-EvalFile ([IO.File]::ReadAllText($Path))
        }
        default {
            $folder = Join-Path $env:LOCALAPPDATA 'Cortex\eval-edit'
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
            $local = Join-Path $folder ([guid]::NewGuid().ToString('N') + '-' + $Name)
            [IO.File]::WriteAllText($local, (Read-EvalFile))
            $before = Get-TextHash $local

            $editorFile = $Editor
            $editorArguments = ''
            if (-not (Test-Path -LiteralPath $Editor)) {
                $parts = [regex]::Match($Editor.Trim(), '^(?:"([^"]+)"|(\S+))\s*(.*)$')
                $editorFile = if ($parts.Groups[1].Success) { $parts.Groups[1].Value } else { $parts.Groups[2].Value }
                $editorArguments = $parts.Groups[3].Value
            }

            $started = Get-Date
            Start-Process -FilePath $editorFile -ArgumentList ("$editorArguments `"$local`"").Trim() -Wait
            # Single-instance editors (VS Code without --wait, a running Notepad++) hand the file to the open window and return at once.
            if (((Get-Date) - $started).TotalSeconds -lt 3) {
                [void](Read-Host "The editor returned immediately. Press Enter when you have saved and closed $Name")
            }

            if ((Get-TextHash $local) -eq $before) {
                Write-Host "$Name unchanged; nothing uploaded."
            }
            else {
                $keep = $true
                Write-EvalFile ([IO.File]::ReadAllText($local))
                $keep = $false
            }
        }
    }
}
finally {
    $plain = $null
    if ($local) {
        if ($keep) {
            Write-Warning "The upload failed; your edits are still in $local (plaintext). Upload them with -Push -Path '$local' and then delete the file."
        }
        else {
            Remove-LocalCopy $local
        }
    }
}