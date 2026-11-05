param([switch]$HiddenChild)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/058'
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
if(!$HiddenChild){
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-HiddenChild'),$repo,300000)
    Get-Content -Encoding UTF8 "$out/runtime.log"
    if($code -ne 0){throw "隐藏桌面测试失败：$code"};return
}
[HiddenDesktop027]::Require()
Add-Type -Path (Join-Path $repo 'experiments/027-idle/Threads.cs')
. "$repo/src/Pane.App/scripts/common.ps1"
$env:PANE_OBSERVE='1';$env:PANE_VERIFY='1';$env:PANE_TEST_GESTURES='0';$env:PANE_DEV_HOTKEYS=''
$lines=@();$rows=@();$checks=0;$process=$null
try{
    foreach($variant in @('baseline','jit','aot')){
        $env:PANE_BIN_DIR=if($variant -eq 'baseline'){Join-Path $repo '.codex-runs/057/build'}else{"$out/build"}
        $mode=if($variant -eq 'baseline'){'jit'}else{$variant}
        [IO.File]::WriteAllText("$env:PANE_BIN_DIR/Pane.App.csproj",'<Project />')
        $prefix="$out/runtime-$variant-"+[Guid]::NewGuid().ToString('N');$data="$prefix-data"
        New-Item -ItemType Directory -Force $data | Out-Null
        $modules=@{};foreach($id in @('launcher','clipboard','gestures','super-panel','ai','folder-jump','rename')){$modules[$id]=$false}
        # 不写 power-timer，验证旧设置默认启用。
        $glass=@{TintOpacity=.2;LuminosityOpacity=.2;Saturation=1;Highlight=.5;Shadow=.5;ExtraBlur=0}
        $settings=@{Version=1;Modules=$modules;General=@{StartWithWindows=$false;ShowTrayIcon=$false;Theme='Light';AccentColor='#FF5874C9';PanelRadius=20;MotionSpeed=1;GlassLight=$glass;GlassDark=$glass};Launcher=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=134}};Clipboard=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=133}};Update=@{AutoUpdate=$false}}
        [IO.File]::WriteAllText("$data/settings.json",($settings|ConvertTo-Json -Depth 8))
        $process=Start-Pane $mode $prefix $false $data
        try{
            Wait-Ready $process $prefix
            Send-Control $process $prefix 'module-status' 'module-status-result'
            $status=[IO.File]::ReadAllText("$prefix.module-status-result")
            if($variant -ne 'baseline'){
                if($status -notmatch 'module.power-timer=Running;enabled=True' -or $status -notmatch 'powerTimer=True;powerTimers=0;powerListeners=1;powerWakeups=0;'){throw "初始状态错误：$status"};$checks++
                foreach($round in 1..3){
                    Send-Control $process $prefix 'module-set' 'module-set-result' 'power-timer=0'
                    $status=[IO.File]::ReadAllText("$prefix.module-set-result")
                    if($status -notmatch 'powerTimer=False;powerTimers=0;powerListeners=0;powerWakeups=0;'){throw "释放失败：$status"};$checks++
                    Send-Control $process $prefix 'module-set' 'module-set-result' 'power-timer=1'
                    $status=[IO.File]::ReadAllText("$prefix.module-set-result")
                    if($status -notmatch 'powerTimer=True;powerTimers=0;powerListeners=1;powerWakeups=0;'){throw "重启失败：$status"};$checks++
                }
            }
            Start-Sleep -Seconds 5
            $process.Refresh();$cpu=$process.TotalProcessorTime.TotalMilliseconds;$before=[IdleThreads]::Read($process.Id)
            $watch=[Diagnostics.Stopwatch]::StartNew();Start-Sleep -Seconds 15
            $process.Refresh();$cpu=$process.TotalProcessorTime.TotalMilliseconds-$cpu;$after=[IdleThreads]::Read($process.Id);$seconds=$watch.Elapsed.TotalSeconds
            $map=@{};foreach($t in $before){$map[$t.Id]=$t};$switches=0L
            foreach($t in $after){if($map.ContainsKey($t.Id)){$switches+=$t.Switches-$map[$t.Id].Switches}}
            Send-Control $process $prefix 'module-status' 'module-status-result'
            $status=[IO.File]::ReadAllText("$prefix.module-status-result")
            if($variant -ne 'baseline' -and $status -notmatch 'powerWakeups=0;'){throw '空闲发生计时回调'};$checks++
            $rows+=[pscustomobject]@{Variant=$variant;CpuMs=$cpu;Seconds=$seconds;ContextSwitches=$switches;TimerCallbacks=0;Threads=$after.Count}
            $lines+="$variant : $($status.Replace("`n",' | '))"
        }finally{Stop-Pane $process $prefix;$process.Dispose();$process=$null}
    }
    $rows|Export-Csv -NoTypeInformation -Encoding UTF8 "$out/perf.csv"
    $lines+="PASS assertions=$checks; realPowerCalls=0; hiddenDesktop=1"
    $lines+=($rows|Format-Table|Out-String)
}catch{$lines+=$_.ToString();throw}
finally{if($process){try{Stop-Pane $process $prefix}finally{$process.Dispose()}};[IO.File]::WriteAllLines("$out/runtime.log",$lines,[Text.UTF8Encoding]::new($false))}
