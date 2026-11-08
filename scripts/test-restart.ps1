param([switch]$KeepRunning)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appFile = Join-Path $projectRoot 'dist\CodexBar.exe'
$statusFile = Join-Path $env:LOCALAPPDATA 'CodexBar\runtime-status.json'

function Wait-Healthy([int]$expectedId, [int]$minimumAttempts) {
    $deadline = [DateTime]::UtcNow.AddSeconds(75)
    do {
        if (Test-Path -LiteralPath $statusFile) {
            try { $status = Get-Content -LiteralPath $statusFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $status = $null }
            if ($status -and $status.processId -eq $expectedId -and -not $status.busy -and $status.attempts -ge $minimumAttempts) {
                if ($status.source -ne 'app-server' -or $status.stage -ne 'ready' -or $status.failure) {
                    throw "Application failed online refresh: $($status.stage), $($status.failure)"
                }
                if ($status.interfaceMode -ne 'codex-tray-only' -or $status.panelVisible) { throw 'A startup or refresh opened an unwanted panel.' }
                return $status
            }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Application did not complete an online refresh within 75 seconds.'
}

function Stop-Gracefully($runningApp, $status) {
    $commandProcess = Start-Process -FilePath $appFile -ArgumentList '--quit' -WindowStyle Hidden -PassThru
    if (-not $commandProcess.WaitForExit(10000)) { throw 'Quit command did not finish.' }
    if (-not $runningApp.WaitForExit(10000)) { throw 'Application did not exit through its normal Quit handler.' }
    if ($status.queryProcessId) {
        $queryProcess = Get-Process -Id $status.queryProcessId -ErrorAction SilentlyContinue
        if ($queryProcess -and -not $queryProcess.WaitForExit(3000)) { throw 'Owned quota process survived normal app exit.' }
    }
    Write-Output "Normal exit released app $($runningApp.Id) and quota process $($status.queryProcessId)."
}

$existing = Get-Process -Name CodexBar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appFile }
foreach ($previous in $existing) {
    $previousStatus = Wait-Healthy $previous.Id 1
    Stop-Gracefully $previous $previousStatus
}

for ($cycle = 1; $cycle -le 3; $cycle++) {
    $start = @{FilePath=$appFile; WindowStyle='Hidden'; PassThru=$true; WorkingDirectory=$env:WINDIR}
    if ($cycle -ne 2) { $start.ArgumentList = '--background' }
    $runningApp = Start-Process @start
    $status = Wait-Healthy $runningApp.Id 1
    Write-Output "Cycle ${cycle}: startup refreshed from $($status.source) in app $($runningApp.Id)."
    $attempts = [int]$status.attempts + 1
    $refreshCommand = Start-Process -FilePath $appFile -ArgumentList '--refresh' -WindowStyle Hidden -PassThru
    if (-not $refreshCommand.WaitForExit(10000)) { throw 'Refresh command did not finish.' }
    $status = Wait-Healthy $runningApp.Id $attempts
    Write-Output "Cycle ${cycle}: manual refresh completed, attempt $($status.attempts), weekly $($status.weeklyRemaining)%, five-hour $($status.fiveHourRemaining)%."
    if ($cycle -lt 3) { Stop-Gracefully $runningApp $status }
}
if (-not $KeepRunning) { Stop-Gracefully $runningApp $status }
Write-Output 'PASS: 3 full starts and manual refreshes, with normal exits and owned-child cleanup.'
