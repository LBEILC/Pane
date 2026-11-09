param([ValidateSet('jit','aot')][string]$Mode='jit',[switch]$HiddenChild)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/093'
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
if(!$HiddenChild){
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Mode',$Mode,'-HiddenChild'),$repo,60000)
    if(Test-Path "$out/hotkeys-$Mode.log"){Get-Content -Encoding UTF8 "$out/hotkeys-$Mode.log"}
    if($code -ne 0){throw "分词热键测试失败：$code"};return
}
[HiddenDesktop027]::Require()
. "$repo/src/Pane.App/scripts/common.ps1"
$env:PANE_BIN_DIR="$out/build";$env:PANE_VERIFY='1';$env:PANE_OBSERVE='1';$env:PANE_DEV_HOTKEYS=''
[IO.File]::WriteAllText("$env:PANE_BIN_DIR/Pane.App.csproj",'<Project />')
$prefix="$out/words-$Mode-"+[Guid]::NewGuid().ToString('N')
$process=$null
try{
    $process=Start-Pane $Mode $prefix $true
    Wait-Ready $process $prefix
    Send-Control $process $prefix 'words-probe' 'words-result'
    [IO.File]::WriteAllText("$out/hotkeys-$Mode.log",[IO.File]::ReadAllText("$prefix.words-result"))
}catch{[IO.File]::WriteAllText("$out/hotkeys-$Mode.log",$_.ToString());throw}
finally{if($process){try{Stop-Pane $process $prefix}finally{$process.Dispose()}}}
