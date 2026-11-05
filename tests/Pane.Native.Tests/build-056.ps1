param([switch]$Baseline)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo '.codex-runs/056'
New-Item -ItemType Directory -Force $out | Out-Null
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/ClipboardImageTests.cpp","/Fo$out/ClipboardImageTests.obj","/Fe$out/ClipboardImageTests.exe",'/link','user32.lib','gdi32.lib','shell32.lib','ole32.lib','uuid.lib','windowscodecs.lib')
if($LASTEXITCODE -ne 0){throw 'Clipboard image tests build failed'}
$arguments=@();if($Baseline){$arguments+='--baseline'}
Push-Location $out
try { & (Join-Path $out 'ClipboardImageTests.exe') @arguments | Tee-Object -FilePath $(if($Baseline){'baseline.log'}else{'images.log'})
 if($LASTEXITCODE -ne 0){throw 'Clipboard image tests failed'}
} finally { Pop-Location }
