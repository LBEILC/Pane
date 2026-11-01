$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo '.codex-runs/044'
New-Item -ItemType Directory -Force $out | Out-Null
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/FolderJumpTests.cpp","/Fo$out/FolderJumpTests.obj","/Fe$out/FolderJumpTests.exe",'/link','user32.lib','advapi32.lib','shell32.lib','shlwapi.lib','ole32.lib','oleaut32.lib','uuid.lib','comdlg32.lib')
if($LASTEXITCODE -ne 0){throw 'Folder jump tests build failed'}
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
$log=Join-Path $out 'hidden.log'
$code=[HiddenDesktop027]::Run((Join-Path $out 'FolderJumpTests.exe'),@($log),$repo,90000)
Get-Content -LiteralPath $log
if($code -ne 0){throw "Folder jump tests failed: $code"}
