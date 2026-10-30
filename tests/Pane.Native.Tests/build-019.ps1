$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo 'src/Pane.App/bin/native'
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/SelectionSnapshotTests.cpp","/Fo$out/SelectionSnapshotTests.obj","/Fe$out/SelectionSnapshotTests.exe",'/link','user32.lib','advapi32.lib','shell32.lib','ole32.lib','oleaut32.lib','uiautomationcore.lib','uuid.lib','urlmon.lib')
if($LASTEXITCODE-ne 0){throw 'Snapshot test build failed'}
& "$out/SelectionSnapshotTests.exe"
if($LASTEXITCODE-ne 0){throw 'Snapshot tests failed'}
