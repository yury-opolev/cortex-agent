<#
.SYNOPSIS
    Copy captured voice samples into the STT-WER fixture set and scaffold the
    manifest (transcript left EMPTY for you to fill — the STT hypothesis is
    shown as an aid; STT output cannot measure STT).

.DESCRIPTION
    Reads %LOCALAPPDATA%\Cortex\voice-samples\*.wav (+ .json sidecars), copies
    them into tests\Cortex.Contained.Speech.Tests\Fixtures\stt\, and writes
    manifest.json with one entry per clip: { file, transcript:"", hypothesis }.
    Idempotent — re-running refreshes the manifest from whatever WAVs are present.

.PARAMETER Source
    Capture directory. Default: %LOCALAPPDATA%\Cortex\voice-samples
.PARAMETER Max
    Optional cap on number of clips to export (newest first).

.EXAMPLE
    .\scripts\Export-VoiceSamples.ps1 -Max 15
#>
param(
    [string]$Source = (Join-Path $env:LOCALAPPDATA "Cortex\voice-samples"),
    [int]$Max = 0
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $PSCommandPath -Parent) -Parent
$dest = Join-Path $repoRoot "tests\Cortex.Contained.Speech.Tests\Fixtures\stt"

if (-not (Test-Path $Source)) {
    throw "No capture directory at $Source. (Legacy 0.2.222 captures lived here; the new pipeline uses /voice-record start in Discord and writes to %LOCALAPPDATA%\Cortex\recording-sessions\<id>\.)"
}
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$wavs = Get-ChildItem -Path $Source -Filter *.wav | Sort-Object LastWriteTime -Descending
if ($Max -gt 0) { $wavs = $wavs | Select-Object -First $Max }
if (-not $wavs) { throw "No .wav files in $Source yet." }

$clips = @()
foreach ($w in $wavs) {
    Copy-Item $w.FullName (Join-Path $dest $w.Name) -Force
    $hyp = ""
    $sidecar = [System.IO.Path]::ChangeExtension($w.FullName, ".json")
    if (Test-Path $sidecar) {
        try { $hyp = (Get-Content $sidecar -Raw | ConvertFrom-Json).sttHypothesis } catch { }
    }
    $clips += [ordered]@{ file = $w.Name; transcript = ""; hypothesis = $hyp }
}

$manifest = [ordered]@{
    "//"     = "transcript is EMPTY on purpose — fill the EXACT words said. hypothesis is the STT guess (an aid, not ground truth)."
    clips    = $clips
}
$manifestPath = Join-Path $dest "manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host "Exported $($clips.Count) clip(s) to $dest" -ForegroundColor Green
Write-Host "Now edit manifest.json: set each 'transcript' to the exact words said." -ForegroundColor Yellow
Write-Host "Then: dotnet test tests/Cortex.Contained.Speech.Tests --filter `"FullyQualifiedName~WerEvalTests`"" -ForegroundColor Gray
