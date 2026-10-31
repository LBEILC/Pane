param([switch]$ProbeOnly,[string]$NativeDll)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo '.codex-runs/034'
New-Item -ItemType Directory -Force $out | Out-Null
if(!$ProbeOnly){
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/HookCacheTests.cpp","/Fo$out/HookCacheTests.obj","/Fe$out/HookCacheTests.exe",'/link','user32.lib')
if($LASTEXITCODE -ne 0){throw 'Cache build failed'}
& "$out/HookCacheTests.exe"
if($LASTEXITCODE -ne 0){throw 'Cache tests failed'}
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/HookStabilityTests.cpp","/Fo$out/HookStabilityTests.obj","/Fe$out/HookStabilityTests.exe",'/link','user32.lib')
if($LASTEXITCODE -ne 0){throw 'Stability build failed'}
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
$code=[HiddenDesktop027]::Run((Join-Path $out 'HookStabilityTests.exe'),@((Join-Path $out 'hidden.log')),$repo,30000)
Get-Content -LiteralPath (Join-Path $out 'hidden.log')
if($code -ne 0){throw "Hidden stability failed: $code"}
}
& cl.exe @('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX',"$PSScriptRoot/RightClickProbe.cpp","/Fo$out/RightClickProbe.obj","/Fe$out/RightClickProbe.exe",'/link','user32.lib')
if($LASTEXITCODE -ne 0){throw 'Probe build failed'}
if($NativeDll){
 if($ProbeOnly){Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')}
 $log=Join-Path $out 'watchdog-hidden.log'
 $code=[HiddenDesktop027]::Run((Join-Path $out 'RightClickProbe.exe'),@('--hidden-watchdog-test',[IO.Path]::GetFullPath($NativeDll),$log),$repo,10000)
 Get-Content -LiteralPath ($log+'.watchdog')
 if($code -ne 0){throw "Hidden watchdog failed: $code"}
 Write-Output 'assertions=1 failures=0 watchdog stopped real hook while UI thread blocked'
 $limitLog=Join-Path $out 'replay-limit-hidden.log'
 $code=[HiddenDesktop027]::Run((Join-Path $out 'RightClickProbe.exe'),@('--hidden-replay-limit-test',[IO.Path]::GetFullPath($NativeDll),$limitLog),$repo,10000)
 Get-Content -LiteralPath $limitLog -Tail 2
 if($code -ne 0){throw "Hidden replay limit failed: $code"}
 Write-Output 'assertions=1 failures=0 probe replay limit stopped real hook; realInput=0'
}
# 只构建，不运行用户桌面的验收入口。
