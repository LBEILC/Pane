$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo 'src/Pane.App/bin/native'
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/CaptureTests.cpp","/Fo$out/CaptureTests.obj","/Fe$out/CaptureTests.exe",'/link',"$out/Pane.Native.lib",'user32.lib')
if($LASTEXITCODE-ne 0){throw 'CaptureTests 构建失败'}
& "$out/CaptureTests.exe"
if($LASTEXITCODE-ne 0){throw 'CaptureTests 失败'}
