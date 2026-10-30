$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo 'src/Pane.App/bin/native'
New-Item -ItemType Directory -Force $out | Out-Null
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/PolicyEventTests.cpp","/Fo$out/PolicyEventTests.obj","/Fe$out/PolicyEventTests.exe",'/link','user32.lib')
if($LASTEXITCODE -ne 0){throw 'PolicyEventTests build failed'}
& "$out/PolicyEventTests.exe"
if($LASTEXITCODE -ne 0){throw 'PolicyEventTests failed'}
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/ProcessPresenceTests.cpp","/Fo$out/ProcessPresenceTests.obj","/Fe$out/ProcessPresenceTests.exe")
if($LASTEXITCODE -ne 0){throw 'ProcessPresenceTests build failed'}
& "$out/ProcessPresenceTests.exe"
if($LASTEXITCODE -ne 0){throw 'ProcessPresenceTests failed'}
