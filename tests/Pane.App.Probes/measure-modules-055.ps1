param([switch]$HiddenChild)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/055'
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
if(!$HiddenChild){
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-HiddenChild'),$repo,900000)
    if(Test-Path "$out/perf-error.log"){Get-Content -Encoding UTF8 "$out/perf-error.log"}
    if($code -ne 0){throw "性能测试失败：$code"}
    Get-Content -Encoding UTF8 "$out/perf.csv";return
}
[HiddenDesktop027]::Require()
function Status($process){
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','Pane-release-probe-'+$process.Id,[IO.Pipes.PipeDirection]::In)
    try{$pipe.Connect(100);$reader=[IO.StreamReader]::new($pipe);$task=$reader.ReadLineAsync();if(!$task.Wait(3000)){throw '状态读取超时'};return $task.Result}finally{$pipe.Dispose()}
}
$env:PANE_OBSERVE='1';$env:PANE_VERIFY='1';$env:PANE_TEST_GESTURES='0';$env:PANE_DEMO='0';$env:PANE_RESULT='';$env:PANE_DEV_HOTKEYS=''
$ids=@('launcher','clipboard','gestures','super-panel','ai','folder-jump','rename')
$rows=@();$process=$null
try{
    foreach($round in 1..3){foreach($variant in @('baseline','all','none','clipboard-gestures','launcher')){
        $binary=if($variant -eq 'baseline'){"$out/baseline"}else{"$out/build"}
        [IO.File]::WriteAllText("$binary/Pane.App.csproj",'<Project />')
        $data="$out/perf-$variant-$round-"+[Guid]::NewGuid().ToString('N');New-Item -ItemType Directory -Force $data|Out-Null
        $env:PANE_DATA_DIR=$data;$env:PANE_INSTANCE=$data
        $enabled=switch($variant){'none'{@()};'clipboard-gestures'{@('clipboard','gestures')};'launcher'{@('launcher')};default{$ids}}
        $modules=@{};foreach($id in $ids){$modules[$id]=$enabled -contains $id}
        $glass=@{TintOpacity=.2;LuminosityOpacity=.2;Saturation=1;Highlight=.5;Shadow=.5;ExtraBlur=0}
        $settings=@{Version=1;Modules=$modules;General=@{StartWithWindows=$false;ShowTrayIcon=$false;Theme='Light';AccentColor='#FF5874C9';PanelRadius=20;MotionSpeed=1;GlassLight=$glass;GlassDark=$glass};Launcher=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=134}};Clipboard=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=135}};Update=@{AutoUpdate=$false}}
        [IO.File]::WriteAllText("$data/settings.json",($settings|ConvertTo-Json -Depth 8))
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $process=Start-Process -FilePath "$binary/aot/Pane.App.exe" -ArgumentList @('--autostart') -WindowStyle Hidden -PassThru
        try{
            $status=''
            do{if($process.HasExited){throw "性能进程提前退出：$variant/$($process.ExitCode)"};try{$status=Status $process}catch [TimeoutException]{};if($watch.Elapsed.TotalSeconds -gt 45){throw '性能启动超时'}}while($status -notmatch 'ready=True')
            $startup=$watch.Elapsed.TotalMilliseconds
            if($enabled -contains 'launcher'){
                while(!(Test-Path "$data/launcher-index.json")){if($process.HasExited -or $watch.Elapsed.TotalSeconds -gt 90){throw '索引未完成'};Start-Sleep -Milliseconds 100}
            }
            Start-Sleep -Seconds 8
            $process.Refresh()
            $rows+=[pscustomobject]@{Variant=$variant;Run=$round;ReadyMs=$startup;WorkingMiB=$process.WorkingSet64/1MB;Threads=$process.Threads.Count;Status=$status}
            $rows|Export-Csv -NoTypeInformation -Encoding UTF8 "$out/perf.csv"
        }finally{
            if(!$process.HasExited){
                $exitProcess=Start-Process -FilePath "$binary/aot/Pane.App.exe" -ArgumentList @('--exit') -WindowStyle Hidden -PassThru
                try{if(!$exitProcess.WaitForExit(12000) -or $exitProcess.ExitCode -ne 0){throw '退出请求失败'}}finally{if(!$exitProcess.HasExited){$exitProcess.Kill()};$exitProcess.Dispose()}
            }
            if(!$process.WaitForExit(3000)){$process.Kill();$process.WaitForExit();throw '未正常清理'}
            if($process.ExitCode -ne 0){throw "退出码 $($process.ExitCode)"}
            $process.Dispose();$process=$null
        }
    }}
}catch{[IO.File]::WriteAllText("$out/perf-error.log",$_.ToString());throw}
finally{if($process -and !$process.HasExited){$process.Kill();$process.WaitForExit()}}
