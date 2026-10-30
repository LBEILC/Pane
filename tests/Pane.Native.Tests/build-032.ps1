param([switch]$Before)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs=& (Find-PaneVsWhere) @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$out=Join-Path $repo '.codex-runs/032'
New-Item -ItemType Directory -Force $out | Out-Null
$options=@('/nologo','/utf-8','/std:c++17','/EHsc','/MT','/W4','/WX')
if($Before){
 $baseline=New-Item -ItemType Directory -Force (Join-Path $out 'before')
 foreach($name in @('Hook.cpp','Capture.h','PolicyEvents.h','ProcessPresence.h')){
  $content=& git @('-C',$repo,'show',"5080f0cb58bd99aaf41e8cbae33ca8719910260b:src/Pane.Native/$name")
  if($LASTEXITCODE -ne 0){throw 'Cannot read pre-fix source'}
  [IO.File]::WriteAllText((Join-Path $baseline.FullName $name),($content -join "`n"),[Text.UTF8Encoding]::new($false))
 }
 $options+='/DPANE_BEFORE'
}
& cl.exe @options @("$PSScriptRoot/PressPolicyTests.cpp","/Fo$out/PressPolicyTests.obj","/Fe$out/PressPolicyTests.exe",'/link','user32.lib')
if($LASTEXITCODE -ne 0){throw 'PressPolicyTests build failed'}
& "$out/PressPolicyTests.exe"
if($Before){if($LASTEXITCODE -eq 0){throw 'Expected reproduction to fail before fix'}}elseif($LASTEXITCODE -ne 0){throw 'PressPolicyTests failed'}
