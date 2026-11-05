param([ValidateSet('jit','aot')][string]$Mode='jit',[switch]$HiddenChild,[switch]$Modules)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/059'
$name=if($Modules){"modules-$Mode"}else{"host-$Mode"}
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
if(!$HiddenChild){
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Mode',$Mode,'-HiddenChild')
    if($Modules){$arguments+='-Modules'}
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,$arguments,$repo,90000)
    Get-Content -LiteralPath "$out/$name.log"
    if($code -ne 0){throw "隐藏桌面宿主测试失败：$code"}
    return
}
[HiddenDesktop027]::Require()
. "$repo/src/Pane.App/scripts/common.ps1"
$env:PANE_BIN_DIR="$out/build"
# 只给本轮构建目录添加开发模式标记，控制通道不能指向用户安装。
[IO.File]::WriteAllText("$env:PANE_BIN_DIR/Pane.App.csproj",'<Project />')
$env:PANE_OBSERVE='0';$env:PANE_VERIFY='1';$env:PANE_DEV_HOTKEYS=''
$prefix="$out/$name-"+[Guid]::NewGuid().ToString('N');$process=$null
try {
    if($Modules){
        $env:PANE_OBSERVE='1';$data=$prefix+'-data';New-Item -ItemType Directory -Force $data | Out-Null
        $glass=@{TintOpacity=.2;LuminosityOpacity=.2;Saturation=1;Highlight=.5;Shadow=.5;ExtraBlur=0}
        $settings=@{Version=1;Modules=@{launcher=$false;clipboard=$true;gestures=$false;'super-panel'=$false;ai=$false;'folder-jump'=$false;rename=$false;'power-timer'=$false};General=@{StartWithWindows=$false;ShowTrayIcon=$false;Theme='Light';AccentColor='#FF5874C9';PanelRadius=20;MotionSpeed=1;GlassLight=$glass;GlassDark=$glass};Launcher=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=134}};Clipboard=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=135};Enabled=$true};Update=@{AutoUpdate=$false}}
        [IO.File]::WriteAllText("$data/settings.json",($settings|ConvertTo-Json -Depth 8))
    }
    $process=Start-Pane $Mode $prefix (!$Modules)
    Wait-Ready $process $prefix
    if($Modules){
        if([IO.File]::ReadAllText("$prefix.ready") -notmatch 'panelkeys=0'){throw '初始钩子未关闭'}
        Send-Control $process $prefix 'panelkeys-begin' 'panelkeys-begin-result'
        if([IO.File]::ReadAllText("$prefix.panelkeys-begin-result") -ne 'begin=True;panelkeys=1'){throw '启动模块未赋值服务'}
        Send-Control $process $prefix 'module-set' 'module-set-result' 'clipboard=0'
        $status=[IO.File]::ReadAllText("$prefix.module-set-result")
        if($status -notmatch 'module.clipboard=Disabled;' -or $status -notmatch 'panelkeys=0'){throw '停用模块未释放钩子'}
        Send-Control $process $prefix 'module-set' 'module-set-result' 'clipboard=1'
        if([IO.File]::ReadAllText("$prefix.module-set-result") -notmatch 'module.clipboard=Running;'){throw '模块未重新启动'}
        Send-Control $process $prefix 'panelkeys-begin' 'panelkeys-begin-result'
        if([IO.File]::ReadAllText("$prefix.panelkeys-begin-result") -ne 'begin=True;panelkeys=1'){throw '重启模块未赋值服务'}
        $result='PASS assertions=5;panelkeys=1;observedClipboardOnly=1;input=0;awaitingExit=1'
    }else{
        Send-Control $process $prefix 'panelkeys-probe' 'panelkeys-result'
        $result=[IO.File]::ReadAllText("$prefix.panelkeys-result")
    }
    $env:PANE_INSTANCE=$prefix;$env:PANE_DATA_DIR=$prefix+'-data'
    $exitProcess=Start-Process -FilePath "$env:PANE_BIN_DIR/$Mode/Pane.App.exe" -ArgumentList @('--exit') -WindowStyle Hidden -PassThru
    try{if(!$exitProcess.WaitForExit(12000) -or $exitProcess.ExitCode -ne 0){throw '--exit 请求失败'}}finally{if(!$exitProcess.HasExited){$exitProcess.Kill()};$exitProcess.Dispose()}
    if(!$process.WaitForExit(2000) -or $process.ExitCode -ne 0){throw '宿主未退出'}
    [IO.File]::WriteAllText("$out/$name.log",$result+"`nPASS --exit;processExited=1;remainingHooks=0")
}catch{[IO.File]::WriteAllText("$out/$name.log",$_.ToString());throw}
finally{Stop-Pane $process $prefix}
