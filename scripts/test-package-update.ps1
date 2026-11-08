param([switch]$KeepRunning)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appFile = Join-Path $projectRoot 'dist\CodexBar.exe'
$dataFolder = Join-Path $env:LOCALAPPDATA 'CodexBar'
$statusFile = Join-Path $dataFolder 'runtime-status.json'
$cacheFile = Join-Path $dataFolder 'codex-cli.path'

function Stop-OwnedApplication {
    $instances = Get-Process -Name CodexBar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appFile }
    foreach ($instance in $instances) {
        $command = Start-Process -FilePath $appFile -ArgumentList '--quit' -WindowStyle Hidden -PassThru
        if (-not $command.WaitForExit(10000) -or -not $instance.WaitForExit(10000)) { throw 'Normal application exit failed.' }
    }
}

# This mutates only the application's generated CLI-path cache; never its account settings.
# No online probe is run before starting the app.
$obsoleteCli = Join-Path $env:ProgramFiles 'WindowsApps\OpenAI.Codex_0.0.0.0_x64__2p2nqsd0c76g0\app\resources\codex.exe'
if (Test-Path -LiteralPath $obsoleteCli) { throw 'The simulated obsolete package unexpectedly exists.' }
if (Test-Path -LiteralPath $cacheFile) { $originalCache = Get-Content -LiteralPath $cacheFile -Raw -Encoding UTF8 } else { $originalCache = $null }
$passed = $false
try {
    foreach ($mode in @('obsolete', 'empty')) {
        Stop-OwnedApplication
        New-Item -ItemType Directory -Path $dataFolder -Force | Out-Null
        if ($mode -eq 'obsolete') { [IO.File]::WriteAllText($cacheFile, $obsoleteCli) }
        else { [IO.File]::WriteAllText($cacheFile, '') }
        $app = Start-Process -FilePath $appFile -ArgumentList '--background' -WorkingDirectory $env:WINDIR -WindowStyle Hidden -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(75)
        $healthy = $false
        do {
            try { $status = Get-Content -LiteralPath $statusFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $status = $null }
            if ($status -and $status.processId -eq $app.Id -and -not $status.busy -and $status.attempts -ge 1) {
                if ($status.source -ne 'app-server' -or $status.failure -or $status.discoverySource -ne 'registered-package' -or $status.isElevated) {
                    throw "Failed update recovery: mode=$mode, stage=$($status.stage), source=$($status.discoverySource), elevated=$($status.isElevated), failure=$($status.failure)"
                }
                if (-not (Test-Path -LiteralPath $status.executable)) { throw 'Resolved CLI is missing.' }
                if ((Get-Content -LiteralPath $cacheFile -Raw -Encoding UTF8).Trim() -ne $status.executable) { throw 'App did not repair the generated cache.' }
                Write-Output "PASS: ${mode} cache -> registered package -> online quota; app $($app.Id), non-elevated; weekly $($status.weeklyRemaining)%, five-hour $($status.fiveHourRemaining)%."
                $healthy = $true; break
            }
            Start-Sleep -Milliseconds 150
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not $healthy) { throw "Timed out testing $mode cache." }
    }
    $passed = $true
    Write-Output 'PASS: obsolete-cache and empty-cache recovery.'
}
finally {
    if (-not $passed -and $null -ne $originalCache) { [IO.File]::WriteAllText($cacheFile, $originalCache) }
    if (-not $KeepRunning) { Stop-OwnedApplication }
}
