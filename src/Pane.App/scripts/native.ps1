$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root '../../scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
if(!$vs){throw '缺少 C++ 构建工具；不安装软件。'}
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$native=Join-Path $root 'bin/native'
New-Item -ItemType Directory -Force $native | Out-Null
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/LD',"$root/Native/Effect.cpp","/Fo$native/",'/link','/PDBALTPATH:%_PDB%',"/OUT:$native/Pane.Effect.dll",'windowsapp.lib','d2d1.lib','dxguid.lib',"/IMPLIB:$native/Pane.Effect.lib")
if($LASTEXITCODE-ne 0){throw '原生效果构建失败'}
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/LD',"$root/Native/DesktopCapture.cpp","/Fo$native/",'/link','/PDBALTPATH:%_PDB%',"/OUT:$native/Pane.Capture.dll",'d3d11.lib','dxgi.lib','windowsapp.lib','user32.lib',"/IMPLIB:$native/Pane.Capture.lib")
if($LASTEXITCODE-ne 0){throw 'DXGI 截图辅助库构建失败'}

& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX','/LD',"$root/../Pane.Native/Hook.cpp","$root/../Pane.Native/System.cpp","$root/../Pane.Native/Selection.cpp","$root/../Pane.Native/Launcher.cpp","$root/../Pane.Native/IconCodec.cpp","$root/../Pane.Native/Clipboard.cpp","$root/../Pane.Native/Setup.cpp","$root/../Pane.Native/Fonts.cpp","/Fo$native/",'/link','/PDBALTPATH:%_PDB%',"/OUT:$native/Pane.Native.dll",'user32.lib','gdi32.lib','windowscodecs.lib','powrprof.lib','propsys.lib','advapi32.lib','urlmon.lib','shell32.lib','shlwapi.lib','ole32.lib','oleaut32.lib','uiautomationcore.lib','uuid.lib','dwrite.lib',"/IMPLIB:$native/Pane.Native.lib")
if($LASTEXITCODE-ne 0){throw '原生手势与选区模块构建失败'}
