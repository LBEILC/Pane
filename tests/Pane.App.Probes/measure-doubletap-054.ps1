param([switch]$HiddenChild)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/054'
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
if(!$HiddenChild){
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-HiddenChild'),$repo,360000)
    if(Test-Path "$out/perf-error.log"){Get-Content -Encoding UTF8 "$out/perf-error.log"}
    if($code -ne 0){throw "隐藏桌面性能测试失败：$code"}
    Get-Content -Encoding UTF8 "$out/perf.csv"
    return
}
[HiddenDesktop027]::Require()
Add-Type -Path (Join-Path $repo 'experiments/027-idle/Threads.cs')
function Status($process){
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','Pane-release-probe-'+$process.Id,[IO.Pipes.PipeDirection]::In)
    try{$pipe.Connect(1000);$reader=[IO.StreamReader]::new($pipe);$task=$reader.ReadLineAsync();if(!$task.Wait(3000)){throw '状态读取超时'};return $task.Result}finally{$pipe.Dispose()}
}
$env:PANE_OBSERVE='1';$env:PANE_VERIFY='1';$env:PANE_DEMO='0';$env:PANE_RESULT='';$env:PANE_MEMORY_STARTUP_LEVEL='6';$env:PANE_DEV_HOTKEYS=''
$rows=@();$process=$null
try{
    foreach($round in 1..3){foreach($variant in @('baseline','chord','double')){
        $binary=if($variant -eq 'baseline'){"$out/baseline"}else{"$out/build"}
        [IO.File]::WriteAllText("$binary/Pane.App.csproj",'<Project />')
        $data="$out/perf-$variant-$round-"+[Guid]::NewGuid().ToString('N');New-Item -ItemType Directory -Force $data | Out-Null
        $env:PANE_DATA_DIR=$data;$env:PANE_INSTANCE=$data
        $glass=@{TintOpacity=0.2;LuminosityOpacity=0.2;Saturation=1;Highlight=0.5;Shadow=0.5;ExtraBlur=0}
        $key=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=134};if($variant -eq 'double'){$key=@{Modifiers='None';VirtualKey=162;DoubleTap=$true}}
        $settings=@{Version=1;General=@{StartWithWindows=$false;ShowTrayIcon=$false;Theme='Light';AccentColor='#FF5874C9';PanelRadius=20;MotionSpeed=1;GlassLight=$glass;GlassDark=$glass};Launcher=@{Hotkey=$key};Clipboard=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=135};Enabled=$false};FolderJump=@{Enabled=$false};SuperPanel=@{Enabled=$false};Update=@{AutoUpdate=$false}}
        [IO.File]::WriteAllText("$data/settings.json",($settings|ConvertTo-Json -Depth 8))
        $process=Start-Process -FilePath "$binary/jit/Pane.App.exe" -ArgumentList @('--autostart') -WindowStyle Hidden -PassThru
        try{
            $deadline=[DateTime]::UtcNow.AddSeconds(45);$status=''
            do{if($process.HasExited){throw '性能进程提前退出'};try{$status=Status $process}catch [TimeoutException]{};if([DateTime]::UtcNow -gt $deadline){throw '性能启动超时'};if($status -notmatch 'ready=True'){Start-Sleep -Milliseconds 50}}while($status -notmatch 'ready=True')
            if($status -notmatch 'visible=0;' -or $status -notmatch 'clipboard=False;' -or $status -notmatch 'tray=False;'){throw "隔离状态不符：$status"}
            $startup=[double]([regex]::Match($status,'hotkeysMs=([0-9.]+)').Groups[1].Value)
            Start-Sleep -Seconds 8
            $process.Refresh();$working=$process.WorkingSet64/1MB;$before=[IdleThreads]::Read($process.Id);$cpu=$process.TotalProcessorTime.TotalMilliseconds
            $watch=[Diagnostics.Stopwatch]::StartNew();Start-Sleep -Seconds 10
            $process.Refresh();$cpu=$process.TotalProcessorTime.TotalMilliseconds-$cpu;$seconds=$watch.Elapsed.TotalSeconds;$after=[IdleThreads]::Read($process.Id)
            $switches=0L;$hookSwitches=0L;$hookCpu=0.0;$map=@{};foreach($item in $before){$map[$item.Id]=$item}
            foreach($item in $after){if($map.ContainsKey($item.Id)){$delta=$item.Switches-$map[$item.Id].Switches;$switches+=$delta;if($item.Name -eq 'Pane 双击修饰键'){$hookSwitches+=$delta;$hookCpu+=($item.Cpu-$map[$item.Id].Cpu)*1000}}}
            $hookThreads=@($after|Where-Object Name -eq 'Pane 双击修饰键').Count
            if($hookThreads -ne [int]($variant -eq 'double')){throw "专用线程数量不符：$variant/$hookThreads"}
            $rows+=[pscustomobject]@{Variant=$variant;Run=$round;StartupMs=$startup;WorkingMiB=$working;Threads=$after.Count;CpuMs=$cpu;Seconds=$seconds;ContextSwitches=$switches;KeyThreads=$hookThreads;KeySwitches=$hookSwitches;KeyCpuMs=$hookCpu;Status=$status}
            $rows|Export-Csv -NoTypeInformation -Encoding UTF8 "$out/perf.csv"
        }finally{
            if(!$process.HasExited){$exitProcess=Start-Process -FilePath "$binary/jit/Pane.App.exe" -ArgumentList @('--exit') -WindowStyle Hidden -PassThru;try{if(!$exitProcess.WaitForExit(12000) -or $exitProcess.ExitCode -ne 0){throw '性能进程退出失败'}}finally{if(!$exitProcess.HasExited){$exitProcess.Kill()};$exitProcess.Dispose()}}
            if(!$process.WaitForExit(2000)){$process.Kill();$process.WaitForExit();throw '性能进程未清理'};$process.Dispose();$process=$null
        }
    }}
}catch{[IO.File]::WriteAllText("$out/perf-error.log",$_.ToString());throw}
finally{if($process -and !$process.HasExited){$process.Kill();$process.WaitForExit()}}
