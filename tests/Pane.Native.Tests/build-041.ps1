param([switch]$RequireForeground)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo '.codex-runs/041'
New-Item -ItemType Directory -Force $out | Out-Null
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/LauncherOpenTests.cpp","/Fo$out/LauncherOpenTests.obj","/Fe$out/LauncherOpenTests.exe",'/link','user32.lib','gdi32.lib','windowscodecs.lib','powrprof.lib','propsys.lib','advapi32.lib','shell32.lib','shlwapi.lib','ole32.lib','oleaut32.lib','uuid.lib')
if($LASTEXITCODE){throw 'LauncherOpenTests build failed'}
$arguments=@();if($RequireForeground){$arguments+='--require-foreground'}
& "$out/LauncherOpenTests.exe" @arguments | Tee-Object -FilePath (Join-Path $out 'launcher-open.log')
if($LASTEXITCODE){throw 'LauncherOpenTests failed'}
