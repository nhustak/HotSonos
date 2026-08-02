# Keep HotSonos running. Uses WaitForExit (reliable) instead of polling.
# Stops only after a clean tray Exit ("Exit requested" in last-exit.txt).
#
# Start once at login:
#   powershell -NoProfile -ExecutionPolicy Bypass -File ...\Keep-HotSonos-Alive.ps1
#
# If HotSonos is already running, this host attaches and waits (does NOT kill it).

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
$lock = Join-Path $env:LOCALAPPDATA 'HotSonos\watchdog-host.lock'

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

# Single host instance
try {
    $fs = [System.IO.File]::Open($lock, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $fs.SetLength(0)
    $bytes = [Text.Encoding]::UTF8.GetBytes("pid=$PID started=$(Get-Date -Format o)")
    $fs.Write($bytes, 0, $bytes.Length)
    $fs.Flush()
} catch {
    Write-HostLog "Another host already holds lock - exiting this instance"
    exit 0
}

Write-HostLog "Host v4 starting; hostPid=$PID exe=$exe"

$restarts = 0
while ($true) {
    if (Test-CleanExit) {
        Write-HostLog 'Clean Exit requested - host stopping'
        break
    }

    # Prefer attach to existing process (agent rebuilds / manual start should not thrash)
    $existing = @(Get-Process -Name HotSonos -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 1) {
        Write-HostLog "Multiple HotSonos ($($existing.Count)) - keeping oldest, killing extras"
        $keep = $existing | Sort-Object StartTime | Select-Object -First 1
        $existing | Where-Object { $_.Id -ne $keep.Id } | ForEach-Object {
            Write-HostLog "Killing extra pid=$($_.Id)"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
        $existing = @($keep)
    }

    try {
        if ($existing.Count -eq 1) {
            $p = $existing[0]
            Write-HostLog "Attached to existing pid=$($p.Id) start=$($p.StartTime.ToString('HH:mm:ss'))"
        } else {
            Write-HostLog "Launching HotSonos (restarts=$restarts)"
            $dumpDir = Join-Path $env:LOCALAPPDATA 'HotSonos\dumps'
            try { New-Item -ItemType Directory -Force -Path $dumpDir | Out-Null } catch {}
            # CLR-created minidumps on hard crash (bypasses WER rate-limit in many cases)
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $exe
            $psi.WorkingDirectory = $dir
            $psi.UseShellExecute = $false
            $psi.Environment['DOTNET_DbgEnableMiniDump'] = '1'
            $psi.Environment['DOTNET_DbgMiniDumpType'] = '4'
            $psi.Environment['DOTNET_DbgMiniDumpName'] = (Join-Path $dumpDir 'hotsonos-%p-%t.dmp')
            $psi.Environment['COMPlus_DbgEnableMiniDump'] = '1'
            $psi.Environment['COMPlus_DbgMiniDumpType'] = '4'
            $psi.Environment['COMPlus_DbgMiniDumpName'] = (Join-Path $dumpDir 'hotsonos-%p-%t.dmp')
            $p = [System.Diagnostics.Process]::Start($psi)
            if (-not $p) {
                Write-HostLog 'Process.Start returned null'
                Start-Sleep -Seconds 5
                continue
            }
            Write-HostLog "Running pid=$($p.Id)"
        }

        $p.WaitForExit()
        $code = $null
        try { $code = $p.ExitCode } catch { $code = "err:$($_.Exception.Message)" }
        Write-HostLog "Exited pid=$($p.Id) code=$code at $(Get-Date -Format HH:mm:ss)"
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
    Write-HostLog "Will relaunch in 3s (restart count=$restarts)"
    if ($restarts -ge 30) {
        Write-HostLog "Too many restarts ($restarts) - backing off 60s"
        Start-Sleep -Seconds 60
        $restarts = 0
    } else {
        Start-Sleep -Seconds 3
    }
}

try { $fs.Close(); $fs.Dispose() } catch {}
try { Remove-Item $lock -Force -ErrorAction SilentlyContinue } catch {}
Write-HostLog 'Host stopped'
