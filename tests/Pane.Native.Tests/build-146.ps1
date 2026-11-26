$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=New-Item -Type Directory -Force "$repo/.codex-runs/146/native"
Push-Location $out.FullName
try {
    & cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/FilePicker146.cpp","$repo/src/Pane.Native/FolderPicker.cpp",'/Fe:FilePicker146.exe','/link','user32.lib','shell32.lib','ole32.lib','uuid.lib')
    if($LASTEXITCODE){throw '文件选择框测试构建失败'}
    Add-Type -Path "$repo/experiments/027-idle/HiddenDesktop.cs"
    $code=[HiddenDesktop027]::Run((Join-Path $out.FullName FilePicker146.exe),@(),$out.FullName,20000)
    if($code){throw "隐藏桌面文件选择框验证失败 $code"}
    Write-Output 'PASS 146/147 hidden desktop: single/multiple/save filters and cancellation, save path with default extension'
} finally { Pop-Location }
