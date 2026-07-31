# Restarts HotSonos if it exits uncleanly. Run this once (e.g. at login) while hunting crashes.
# Does not restart after tray Exit (checks last-exit.txt for "Exit requested").

$ErrorActionPreference = 'SilentlyContinue'
$exe = Join-Path $PSScriptRoot '..\src\HotSonos.App\bin\Release\net10.0-windows\HotSonos.exe'
if (-not (Test-Path $exe)) {
    $exe = Join-Path $PSScriptRoot '..\src\HotSonos.App\bin\Debug\net10.0-windows\HotSonos.exe'
}
$lastExit = Join-Path $env:LOCALAPPDATA 'HotSonos\last-exit.txt'
$log = Join-Path $env:LOCALAPPDATA 'HotSonos\watchdog-host.log'

function Write-HostLog([string]$msg) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $msg"
    Add-Content -Path $log -Value $line
}

Write-HostLog "Host starting; exe=$exe"
while ($true) {
    $running = Get-Process -Name HotSonos -ErrorAction SilentlyContinue
    if (-not $running) {
        $txt = ''
        if (Test-Path $lastExit) { $txt = Get-Content $lastExit -Raw }
        if ($txt -match 'Exit requested') {
            Write-HostLog 'Clean exit detected — host stopping'
            break
        }
        Write-HostLog 'HotSonos not running — starting'
        Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
        Start-Sleep -Seconds 8
        continue
    }
    Start-Sleep -Seconds 10
}
