param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = '',
    [string]$VcVarsPath = ''
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The optional native skinning library is built for Windows x64.'
}
$taskRepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $taskRepoRoot "src/LCReplay.Plugin/bin/$Configuration/netstandard2.1"
}
$taskOutputPath = [IO.Path]::GetFullPath($OutputDirectory)
$taskBuildPath = Join-Path $taskRepoRoot "src/LCReplay.Skinning.Native/obj/$Configuration"
$taskSourcePath = Join-Path $taskRepoRoot 'src/LCReplay.Skinning.Native/LCReplay.Skinning.cpp'

# Windows plugin builds invoke this script automatically. Managed-only builds
# can opt out with -p:BuildNativeSkinning=false.
if ([string]::IsNullOrWhiteSpace($VcVarsPath)) {
    $taskVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $taskVsWhere) {
        $taskVsInstall = & $taskVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($LASTEXITCODE -eq 0 -and $taskVsInstall) {
            $VcVarsPath = Join-Path ($taskVsInstall | Select-Object -First 1) 'VC/Auxiliary/Build/vcvars64.bat'
        }
    }
}
if ([string]::IsNullOrWhiteSpace($VcVarsPath) -or !(Test-Path -LiteralPath $VcVarsPath)) {
    throw 'Native skinning requires the Visual Studio C++ x64 build tools. Install them, or build managed-only with -p:BuildNativeSkinning=false.'
}
$VcVarsPath = (Resolve-Path -LiteralPath $VcVarsPath).Path
foreach ($taskPath in @($VcVarsPath, $taskOutputPath, $taskBuildPath, $taskSourcePath)) {
    # These characters are not safely representable in this generated batch
    # file; reject them instead of allowing command or environment expansion.
    if ($taskPath.IndexOfAny([char[]]'"%') -ge 0 -or $taskPath.Contains("`r") -or $taskPath.Contains("`n")) {
        throw "Unsupported character in native build path: $taskPath"
    }
}
New-Item -ItemType Directory -Force -Path $taskOutputPath, $taskBuildPath | Out-Null
$taskDll = Join-Path $taskOutputPath 'LCReplay.Skinning.dll'
$taskObj = Join-Path $taskBuildPath 'LCReplay.Skinning.obj'
$taskPdb = Join-Path $taskBuildPath 'LCReplay.Skinning.pdb'
$taskImportLibrary = Join-Path $taskBuildPath 'LCReplay.Skinning.lib'
$taskCompilerFlags = if ($Configuration -eq 'Release') { '/O2' } else { '/Od /Zi' }
$taskLinkFlags = if ($Configuration -eq 'Debug') { '/DEBUG' } else { '' }
$taskCommandPath = Join-Path $taskBuildPath 'build-native.cmd'
$taskCommand = @"
@echo off
chcp 65001 >nul
setlocal DisableDelayedExpansion
call "$VcVarsPath" >nul
if errorlevel 1 exit /b %errorlevel%
cl.exe /nologo /std:c++17 $taskCompilerFlags /fp:precise /EHsc /MT /LD /W4 /WX "$taskSourcePath" /Fo:"$taskObj" /Fd:"$taskPdb" /link /OUT:"$taskDll" /IMPLIB:"$taskImportLibrary" /PDB:"$taskPdb" /INCREMENTAL:NO $taskLinkFlags
exit /b %errorlevel%
"@
[IO.File]::WriteAllText($taskCommandPath, $taskCommand, [Text.UTF8Encoding]::new($false))
& $taskCommandPath
if ($LASTEXITCODE -ne 0) { throw "Native skinning compilation failed with exit code $LASTEXITCODE." }
if (!(Test-Path -LiteralPath $taskDll)) { throw "Native skinning output was not created: $taskDll" }
Write-Output "Built native skinning: $taskDll"
