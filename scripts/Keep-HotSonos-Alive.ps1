# Keep HotSonos running. Uses WaitForExit (reliable) instead of polling.
# Stops only after a clean tray Exit ("Exit requested" in last-exit.txt).
#
# Start once at login:
#   powershell -NoProfile -ExecutionPolicy Bypass -File ...\Keep-HotSonos-Alive.ps1

$ErrorActionPreference = 'SilentlyContinue'
$release = Join-Path $PSScriptRoot '..\src\HotSonos.App\bin\Release\net10.0-windows\HotSonos.exe'
$debug   = Join-Path $PSScriptRoot '..\src\HotSonos.App\bin\Debug\net10.0-windows\HotSonos.exe'
if (Test-Path $release) {
    $exe = (Resolve-Path $release).Path
} elseif (Test-Path $debug) {
    $exe = (Resolve-Path $debug).Path
} else {
    Write-Error "HotSonos.exe not found under Release or Debug"
    exit 1
}
$dir = Split-Path $exe
$lastExit = Join-Path $env:LOCALAPPDATA 'HotSonos\last-exit.txt'
$log = Join-Path $env:LOCALAPPDATA 'HotSonos\watchdog-host.log'

function Write-HostLog([string]$msg) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $msg"
    try { Add-Content -Path $log -Value $line -Encoding UTF8 } catch {}
    Write-Host $line
}

function Test-CleanExit {
    if (-not (Test-Path $lastExit)) { return $false }
    try {
        $t = Get-Content $lastExit -Raw -ErrorAction Stop
        return ($t -match 'Exit requested')
    } catch { return $false }
}

Write-HostLog "Host v3 starting; exe=$exe"

# Ensure single app instance before we take ownership of restarts
Get-Process -Name HotSonos -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$restarts = 0
while ($true) {
    if (Test-CleanExit) {
        Write-HostLog 'Clean Exit requested - host stopping'
        break
    }

    Write-HostLog "Launching HotSonos (restarts=$restarts)"
    try {
        $p = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru
        if (-not $p) {
            Write-HostLog 'Start-Process returned null'
            Start-Sleep -Seconds 5
            continue
        }
        Write-HostLog "Running pid=$($p.Id)"
        $p.WaitForExit()
        $code = $p.ExitCode
        Write-HostLog "Exited pid=$($p.Id) code=$code"
    } catch {
        Write-HostLog "Launch/wait error: $($_.Exception.Message)"
        Start-Sleep -Seconds 5
        continue
    }

    if (Test-CleanExit) {
        Write-HostLog 'Clean Exit requested after exit - host stopping'
        break
    }

    $restarts++
    if ($restarts -ge 30) {
        Write-HostLog "Too many restarts ($restarts) - backing off 60s"
        Start-Sleep -Seconds 60
        $restarts = 0
    } else {
        Start-Sleep -Seconds 3
    }
}
