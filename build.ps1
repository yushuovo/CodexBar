$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'The .NET Framework compiler was not found. Build on Windows with .NET Framework 4.8 installed.' }
$outputPath = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$references = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll')
$sources = @('Core.cs', 'CodexQuotaClient.cs', 'Controls.cs', 'CodexPanel.cs', 'Program.cs') | ForEach-Object { Join-Path $PSScriptRoot "src\$_" }
& $compilerPath /nologo /utf8output /target:winexe /platform:anycpu /optimize+ /win32manifest:"$PSScriptRoot\app.manifest" /out:"$outputPath\CodexBar.exe" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
& $compilerPath /nologo /utf8output /target:exe /platform:anycpu /main:CodexBar.CoreTests /out:"$outputPath\CodexBar.Tests.exe" @references @sources (Join-Path $PSScriptRoot 'tests\CoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
Write-Output "Built: $outputPath\CodexBar.exe"
