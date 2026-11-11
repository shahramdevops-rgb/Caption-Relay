# Speaks a text file with System.Speech (Windows PowerShell 5.1) through the default speakers,
# so Live Captions captions it. Writes $DoneFile when finished. ASCII only (PS 5.1 parsing).
param(
    [Parameter(Mandatory = $true)][string[]]$TextFile,
    [string]$DoneFile = '',
    [int]$Rate = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SetOutputToDefaultAudioDevice()
$synth.Rate = $Rate
$synth.Volume = 100

if ($DoneFile -and (Test-Path $DoneFile)) { Remove-Item $DoneFile -Force }

$start = Get-Date
"Voice: " + $synth.Voice.Name
# With -File, several paths arrive as one comma-separated string.
foreach ($file in ($TextFile -join ',').Split(',')) {
    foreach ($line in Get-Content -Path $file) {
        if ($line.Trim().Length -gt 0) { $synth.Speak($line) }
    }
}
$synth.Dispose()
"Spoken seconds: " + [int]((Get-Date) - $start).TotalSeconds

if ($DoneFile) { Set-Content -Path $DoneFile -Value 'done' -Encoding ASCII }
