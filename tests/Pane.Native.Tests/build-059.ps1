param([switch]$SendInput)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo '.codex-runs/059'
New-Item -ItemType Directory -Force $out | Out-Null
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/PanelKeysTests.cpp","/Fo$out/PanelKeysTests.obj","/Fe$out/PanelKeysTests.exe",'/link','user32.lib')
if($LASTEXITCODE -ne 0){throw 'PanelKeys tests build failed'}
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
$log=Join-Path $out $(if($SendInput){'native-sendinput.log'}else{'native.log'})
$arguments=@($log);if($SendInput){$arguments+='--sendinput'}
$code=[HiddenDesktop027]::Run((Join-Path $out 'PanelKeysTests.exe'),$arguments,$repo,30000)
Get-Content -LiteralPath $log
if($code -ne 0){throw "PanelKeys tests failed: $code"}
