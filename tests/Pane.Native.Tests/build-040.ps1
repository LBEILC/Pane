param([switch]$OwnWindow)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo '.codex-runs/040'
New-Item -ItemType Directory -Force $out | Out-Null
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/FileSelectionTests.cpp","/Fo$out/FileSelectionTests.obj","/Fe$out/FileSelectionTests.exe",'/link','user32.lib','shlwapi.lib','shell32.lib','ole32.lib','oleaut32.lib','uuid.lib')
if($LASTEXITCODE){throw 'FileSelectionTests build failed'}
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
$log=Join-Path $out $(if($OwnWindow){'shell-own-window.log'}else{'shell-hidden.log'})
if($OwnWindow){& (Join-Path $out 'FileSelectionTests.exe') @($log);$code=$LASTEXITCODE}else{$code=[HiddenDesktop027]::Run((Join-Path $out 'FileSelectionTests.exe'),@($log),$repo,45000)}
Get-Content -LiteralPath $log
if($code){throw "FileSelectionTests failed: $code"}
