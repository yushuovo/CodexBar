$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appFile = Join-Path $projectRoot 'dist\CodexBar.exe'
$statusFile = Join-Path $env:LOCALAPPDATA 'CodexBar\runtime-status.json'
$before = Get-Content -LiteralPath $statusFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($before.interfaceMode -ne 'codex-tray-only' -or $before.panelVisible) { throw 'The application is not running in tray-only mode.' }
if (@($before.menuItems).Count -ne 4) { throw 'The context menu must have exactly four commands.' }
$duplicate = Start-Process -FilePath $appFile -WindowStyle Hidden -PassThru
if (-not $duplicate.WaitForExit(10000)) { throw 'Duplicate launch did not return.' }
Start-Sleep -Milliseconds 500
$after = Get-Content -LiteralPath $statusFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($after.processId -ne $before.processId -or $after.panelVisible) { throw 'Duplicate launch started another instance or opened a panel.' }
Write-Output 'PASS: startup and duplicate launch keep the Codex settings panel hidden; the right-click menu has four commands.'
$imagePath = Join-Path $projectRoot 'qa\codex-panel-v0.6.png'
$preview = Start-Process -FilePath $appFile -ArgumentList '--render-panel', $imagePath -WindowStyle Hidden -PassThru
if (-not $preview.WaitForExit(10000) -or $preview.ExitCode -ne 0) { throw 'Panel bitmap render did not complete.' }
if (-not (Test-Path -LiteralPath $imagePath)) { throw 'Panel bitmap is missing.' }
Write-Output "Rendered own UI controls to bitmap: $imagePath"
