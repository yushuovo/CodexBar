param([switch]$KeepRunning)
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$appPath = [IO.Path]::GetFullPath((Join-Path $projectPath 'dist\CodexBar.exe'))
$legacyAppPath = Join-Path $projectPath 'dist\AIAPIBar.exe'
$existing = Get-Process -Name CodexBar,AIAPIBar -ErrorAction SilentlyContinue
foreach ($runningApp in $existing) {
    if ($runningApp.Path -eq $appPath -or $runningApp.Path -eq $legacyAppPath) {
        Write-Output "Previous test instance still running: $($runningApp.Id)"
        $quitRequest = Start-Process -FilePath $runningApp.Path -ArgumentList '--quit' -WindowStyle Hidden -PassThru
        $quitRequest.WaitForExit(5000) | Out-Null
        if (-not $runningApp.WaitForExit(5000)) {
            # Compatibility with builds made before the normal-exit command existed.
            Stop-Process -Id $runningApp.Id
            $runningApp.WaitForExit()
        }
    }
}
& (Join-Path $projectPath 'build.ps1')
& (Join-Path $projectPath 'dist\CodexBar.Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
# Start the actual app before any online probe so a probe cannot repair its cache first.
$cacheFile = Join-Path $env:LOCALAPPDATA 'CodexBar\codex-cli.path'
if (Test-Path -LiteralPath $cacheFile) {
    $cachedCli = (Get-Content -LiteralPath $cacheFile -Raw -Encoding UTF8).Trim()
    Write-Output "Before application startup, cached CLI exists: $(Test-Path -LiteralPath $cachedCli)"
}
$liveApp = Start-Process -FilePath $appPath -WindowStyle Hidden -PassThru
Write-Output "Started CodexBar: $($liveApp.Id)"
$statusFile = Join-Path $env:LOCALAPPDATA 'CodexBar\runtime-status.json'
$deadline = [DateTime]::UtcNow.AddSeconds(75)
do {
    if (Test-Path -LiteralPath $statusFile) {
        try { $status = Get-Content -LiteralPath $statusFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $status = $null }
        if ($status -and $status.processId -eq $liveApp.Id -and -not $status.busy) {
            if ($status.source -ne 'app-server' -or $status.failure -or $status.stage -ne 'ready') {
                throw "Live application failed refresh: $($status.stage), $($status.failure)"
            }
            if ($status.interfaceMode -ne 'codex-tray-only' -or $status.panelVisible) {
                throw 'Application must start monitoring with its merged panel hidden.'
            }
            Write-Output "Live application v$($status.version) verified: app-server, discovery $($status.discoverySource), elevated $($status.isElevated), weekly $($status.weeklyRemaining)%, five-hour $($status.fiveHourRemaining)%."
            if (-not $KeepRunning) {
                $quit = Start-Process -FilePath $appPath -ArgumentList '--quit' -WindowStyle Hidden -PassThru
                $quit.WaitForExit(10000) | Out-Null
                if (-not $liveApp.WaitForExit(10000)) { throw 'The application did not exit after verification.' }
                Write-Output 'Verification finished; application exited.'
            }
            return
        }
    }
    Start-Sleep -Milliseconds 150
} while ([DateTime]::UtcNow -lt $deadline)
throw 'The application itself did not complete an online refresh within 75 seconds.'
