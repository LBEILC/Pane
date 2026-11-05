param([ValidateSet('jit','aot')][string]$Mode='jit',[switch]$HiddenChild)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/055'
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
if(!$HiddenChild){
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Mode',$Mode,'-HiddenChild'),$repo,120000)
    if(Test-Path "$out/hotkeys-$Mode.log"){Get-Content -Encoding UTF8 "$out/hotkeys-$Mode.log"}
    if($code -ne 0){throw "热键回归失败：$code"};return
}
[HiddenDesktop027]::Require()
. "$repo/src/Pane.App/scripts/common.ps1"
$env:PANE_BIN_DIR="$out/build";$env:PANE_VERIFY='1';$env:PANE_OBSERVE='0';$env:PANE_DEV_HOTKEYS=''
[IO.File]::WriteAllText("$env:PANE_BIN_DIR/Pane.App.csproj",'<Project />')
$lines=@();$process=$null
try{
    foreach($probe in @('hotkey','doubletap')){
        $prefix="$out/$probe-$Mode-"+[Guid]::NewGuid().ToString('N')
        $process=Start-Pane $Mode $prefix $true
        try{
            Wait-Ready $process $prefix
            Send-Control $process $prefix "$probe-probe" "$probe-result"
            $lines+=[IO.File]::ReadAllText("$prefix.$probe-result")
        }finally{try{Stop-Pane $process $prefix}finally{$process.Dispose();$process=$null}}
    }
}catch{$lines+=$_.ToString();throw}
finally{[IO.File]::WriteAllLines("$out/hotkeys-$Mode.log",$lines,[Text.UTF8Encoding]::new($false))}
