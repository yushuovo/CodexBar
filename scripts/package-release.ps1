$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$programSource = Get-Content -LiteralPath (Join-Path $projectRoot 'src\Program.cs') -Raw
$versionMatch = [regex]::Match($programSource, 'AssemblyVersion\("(\d+\.\d+\.\d+)\.\d+"\)')
if (-not $versionMatch.Success) { throw 'AssemblyVersion was not found in src/Program.cs.' }
$version = $versionMatch.Groups[1].Value
$notesSource = Join-Path $projectRoot "docs\releases\v$version.md"
if (-not (Test-Path -LiteralPath $notesSource -PathType Leaf)) { throw "Add release notes before packaging: docs/releases/v$version.md" }

& (Join-Path $projectRoot 'build.ps1')
& (Join-Path $projectRoot 'dist\CodexBar.Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Offline checks failed; release files were not updated.' }

$releaseDirectory = Join-Path $projectRoot "release\v$version"
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$executablePath = Join-Path $releaseDirectory 'CodexBar.exe'
$archivePath = Join-Path $releaseDirectory "CodexBar-v$version-windows.zip"
Copy-Item -LiteralPath (Join-Path $projectRoot 'dist\CodexBar.exe') -Destination $executablePath -Force
$packageFiles = @(
    (Join-Path $projectRoot 'dist\CodexBar.exe'),
    (Join-Path $projectRoot 'README.md'),
    (Join-Path $projectRoot 'LICENSE'),
    (Join-Path $projectRoot 'CHANGELOG.md')
)
Compress-Archive -LiteralPath $packageFiles -DestinationPath $archivePath -Force
$checksums = foreach ($assetPath in @($executablePath, $archivePath)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $assetPath)
}
$checksums | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding Ascii
Copy-Item -LiteralPath $notesSource -Destination (Join-Path $releaseDirectory 'RELEASE-NOTES.md') -Force
Write-Output "Release files ready: $releaseDirectory"
Write-Output 'Upload CodexBar.exe, the Windows ZIP and SHA256SUMS.txt as release assets. Paste RELEASE-NOTES.md into the release description.'
