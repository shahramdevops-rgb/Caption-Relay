# Builds ONE self-contained exe and puts it at a fixed place:  build\CaptionRelay.exe  (replaced on every build)
# It contains the app, the .NET runtime and the browser extension; the target PC needs nothing installed.
# Needs the .NET 8 SDK on the build machine only.  Run:  powershell -ExecutionPolicy Bypass -File build-exe.ps1
# If that exe is running it is stopped, replaced and started again (a running exe cannot be overwritten).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$buildDir = Join-Path $root 'build'
$target = Join-Path $buildDir 'CaptionRelay.exe'
$out = Join-Path $root 'app\bin\SingleExe'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'app\CaptionRelay.csproj') -c Release -p:SingleExe=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$extra = Get-ChildItem $out | Where-Object { $_.Name -ne 'CaptionRelay.exe' }
if ($extra) { Write-Warning ("Publish also produced: " + ($extra.Name -join ', ') + " (not needed by the exe)") }

New-Item -ItemType Directory -Force $buildDir | Out-Null

$running = @(Get-Process CaptionRelay -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target })
foreach ($p in $running) {
    "Stopping the running CaptionRelay (pid $($p.Id)) to replace it"
    Stop-Process -Id $p.Id -Force
    $p.WaitForExit(5000) | Out-Null
}

Copy-Item (Join-Path $out 'CaptionRelay.exe') $target -Force
$exe = Get-Item $target
'Built {0} ({1:N1} MB, {2})' -f $exe.FullName, ($exe.Length / 1MB), $exe.LastWriteTime

if ($running.Count -gt 0) {
    Start-Process -FilePath $target -WorkingDirectory $buildDir
    'Started the new build again'
}
