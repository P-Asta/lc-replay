param(
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) {
    & dotnet build (Join-Path $taskRoot 'src\LCReplay.Plugin\LCReplay.Plugin.csproj') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$taskStage = Join-Path $taskRoot "artifacts\package-$taskStamp"
$taskPlugin = Join-Path $taskStage 'BepInEx\plugins\LCReplay'
New-Item -ItemType Directory -Path $taskPlugin -Force | Out-Null
$taskBin = Join-Path $taskRoot "src\LCReplay.Plugin\bin\$Configuration\netstandard2.1"
foreach ($taskDll in @('LCReplay.dll', 'LCReplay.Core.dll')) {
    Copy-Item -LiteralPath (Join-Path $taskBin $taskDll) -Destination $taskPlugin
}
foreach ($taskDoc in @('README.md', 'CHANGELOG.md', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskDoc) -Destination $taskStage
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $taskStage -Recurse
$taskExample = Join-Path $taskRoot 'tools\LCReplay.Cli\examples\synthetic-demo.lcr'
if (Test-Path -LiteralPath $taskExample) {
    $taskExampleDir = Join-Path $taskStage 'examples'
    New-Item -ItemType Directory -Path $taskExampleDir -Force | Out-Null
    Copy-Item -LiteralPath $taskExample -Destination $taskExampleDir
}
$taskVersion = ([xml](Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$taskZip = Join-Path $taskRoot "artifacts\LCReplay-$taskVersion.zip"
Compress-Archive -Path (Join-Path $taskStage '*') -DestinationPath $taskZip -Force
Write-Output $taskZip
