param([string]$Mode='aot',[switch]$Baseline)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
$names=@('PANE_OBSERVE','PANE_VERIFY','PANE_DEV_HOTKEYS','PANE_PSEUDO_LOC')
$saved=@{};$rows=@()
try {
    foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
    $env:PANE_OBSERVE='1';$env:PANE_VERIFY='1';$env:PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C';$env:PANE_PSEUDO_LOC=$null
    for($run=1;$run -le 3;$run++) {
        $prefix=New-Result '048-localization';$data=$prefix+'-data';$process=$null
        New-Item -ItemType Directory -Path $data | Out-Null
        $glass=@{TintOpacity=.2;LuminosityOpacity=.2;Saturation=1;Highlight=1;Shadow=1;ExtraBlur=0}
        $settings=@{Version=1;General=@{StartWithWindows=$false;ShowTrayIcon=$false;Theme='Dark';AccentColor='#FF5874C9';PanelRadius=20;MotionSpeed=1;GlassLight=$glass;GlassDark=$glass;UiLanguage='zh-Hant'};Launcher=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=76}};Clipboard=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=67}};FolderJump=@{Enabled=$false};Update=@{AutoUpdate=$false}}
        [IO.File]::WriteAllText((Join-Path $data 'settings.json'),($settings|ConvertTo-Json -Depth 10))
        try {
            $watch=[Diagnostics.Stopwatch]::StartNew()
            $process=Start-Pane $Mode $prefix $false $data
            Wait-Ready $process $prefix
            $startup=$watch.Elapsed.TotalMilliseconds
            Start-Sleep -Seconds 15
            $process.Refresh()
            $row=[pscustomobject]@{Run=$run;StartupMs=$startup;WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB}
            $rows+=$row
            if(!$Baseline){Send-Control $process $prefix 'localization-probe' 'localization-result'
            $text=[IO.File]::ReadAllLines($prefix+'.localization-result')
            $expected=@('zh-Hant','新版本 1.2.3 已下載好','重新啟動並更新','開啟啟動器','開啟剪貼簿','設定','暫停手勢','結束 Pane')
            if(($text -join '|') -cne ($expected -join '|')){throw "菜单文字不符：$($text -join '|')"}
            Write-Output ($row|ConvertTo-Json -Compress)
            Write-Output ($text -join '|')}
        } finally { Stop-Pane $process $prefix }
    }
    $rows|Export-Csv -NoTypeInformation -Encoding UTF8 "$root/bin/results/048-localization-$Mode-$Baseline.csv"
    Write-Output "PASS baseline=$Baseline; menu assertions=$(if($Baseline){0}else{24}); medianStartupMs=$(($rows.StartupMs|Sort-Object)[1]); medianWorkingMiB=$(($rows.WorkingMiB|Sort-Object)[1]); medianPrivateMiB=$(($rows.PrivateMiB|Sort-Object)[1])"
} finally {foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}}
