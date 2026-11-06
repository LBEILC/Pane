$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo '.codex-runs/066'
New-Item -ItemType Directory -Force $out | Out-Null
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/LauncherOpenPolicyTests.cpp","/Fo$out/LauncherOpenPolicyTests.obj","/Fe$out/LauncherOpenPolicyTests.exe")
if($LASTEXITCODE -ne 0){throw 'LauncherOpenPolicy tests build failed'}
& "$out/LauncherOpenPolicyTests.exe"
if($LASTEXITCODE -ne 0){throw 'LauncherOpenPolicy tests failed'}
