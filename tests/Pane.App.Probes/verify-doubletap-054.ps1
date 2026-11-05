param([ValidateSet('jit','aot')][string]$Mode='jit',[switch]$HiddenChild)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/054'
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
if(!$HiddenChild){
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Mode',$Mode,'-HiddenChild'),$repo,55000)
    Get-Content -LiteralPath "$out/host-$Mode.log"
    if($code -ne 0){throw "隐藏桌面宿主测试失败：$code"}
    return
}
[HiddenDesktop027]::Require()
. "$repo/src/Pane.App/scripts/common.ps1"
$env:PANE_BIN_DIR="$out/build"
# 只给本轮构建目录添加开发模式标记，控制通道不能指向用户安装。
[IO.File]::WriteAllText("$env:PANE_BIN_DIR/Pane.App.csproj",'<Project />')
$env:PANE_OBSERVE='0';$env:PANE_VERIFY='1';$env:PANE_DEV_HOTKEYS=''
$prefix="$out/host-$Mode-"+[Guid]::NewGuid().ToString('N');$process=$null
try {
    $process=Start-Pane $Mode $prefix $true
    Wait-Ready $process $prefix
    Send-Control $process $prefix 'doubletap-probe' 'doubletap-result'
    $result=[IO.File]::ReadAllText("$prefix.doubletap-result")
    $env:PANE_INSTANCE=$prefix;$env:PANE_DATA_DIR=$prefix+'-data'
    $exitProcess=Start-Process -FilePath "$env:PANE_BIN_DIR/$Mode/Pane.App.exe" -ArgumentList @('--exit') -WindowStyle Hidden -PassThru
    try{if(!$exitProcess.WaitForExit(12000) -or $exitProcess.ExitCode -ne 0){throw '--exit 请求失败'}}finally{if(!$exitProcess.HasExited){$exitProcess.Kill()};$exitProcess.Dispose()}
    if(!$process.WaitForExit(2000) -or $process.ExitCode -ne 0){throw '宿主未退出'}
    [IO.File]::WriteAllText("$out/host-$Mode.log",$result+"`nPASS --exit;processExited=1;remainingHooks=0")
}catch{[IO.File]::WriteAllText("$out/host-$Mode.log",$_.ToString());throw}
finally{Stop-Pane $process $prefix}
