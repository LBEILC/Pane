param([ValidateSet('jit','aot')][string]$Mode='jit',[string]$Only='')
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
. "$repo/src/Pane.App/scripts/capture-native.ps1"
. "$repo/src/Pane.App/scripts/capture-functions.ps1"
Add-Type -AssemblyName System.Drawing
$out=Join-Path $repo 'docs/research/refs/049'
New-Item -ItemType Directory -Force $out | Out-Null
$CaptureBackend='gdi'
$cases=@(
    @{Name='launcher-home';Command='launcher';Title='启动器'},
    @{Name='launcher-search';Command='launcher';Title='启动器';Query='py'},
    @{Name='launcher-terminal';Command='launcher';Title='启动器';Query='>p'},
    @{Name='clipboard';Command='clipboard';Title='剪贴板'},
    @{Name='super-text';Command='super';Title='超级面板'},
    @{Name='super-files';Command='super';Title='超级面板';Selection='files'}
)
foreach($section in @('general','launcher','clipboard','gesture','superpanel','dialog','ai','icons','about')) {
    $cases+=@{Name="settings-$section";Command='settings';Title='设置';Section=$section}
}
foreach($pair in @(@('general',650),@('launcher',650),@('launcher',1300),@('clipboard',650),@('gesture',650),@('gesture',1300),@('superpanel',650),@('ai',650),@('ai',1300),@('ai',2000),@('icons',650))) {
    $cases+=@{Name="settings-$($pair[0])-$($pair[1])";Command='settings';Title='设置';Section="$($pair[0])@$($pair[1])"}
}
$cases+=@(
    @{Name='translate';Open='translate';Title='翻译'},
    @{Name='files';Open='files';Title='文件'},
    @{Name='rename';Open='rename';Title='批量重命名'},
    @{Name='folder-jump';Open='folder-jump';Title='跳到文件夹'},
    @{Name='tray';Open='tray';Title='托盘菜单'},
    @{Name='chat';Open='chat';Title='AI 对话'}
)
$names=@('PANE_THEME','PANE_PSEUDO_LOC','PANE_WALLPAPER','PANE_DESIGN_QUERY','PANE_DESIGN_SETTINGS','PANE_DESIGN_OPEN','PANE_DESIGN_SELECTION','PANE_DESIGN_UPDATE','PANE_VERIFY','PANE_OBSERVE')
$saved=@{}
function Stop-Demo($process,[string]$prefix) {
    try { Stop-Pane $process $prefix }
    catch {
        # 既有 Shutdown 把观察模式中 null 手势服务的 != 1 当作失败；不关观察模式。
        # 只接受已退出、没有真实服务、没有异常文件的这一种演示探针误报。
        $ready=if(Test-Path -LiteralPath "$prefix.ready"){[IO.File]::ReadAllText("$prefix.ready")}else{''}
        $stopped=if(Test-Path -LiteralPath "$prefix.stopped"){[IO.File]::ReadAllText("$prefix.stopped")}else{''}
        if($process -and $process.HasExited -and $process.ExitCode-eq 1 -and $stopped-eq 'cleanup-failed' -and
           $ready -match 'demo=1;hotkeys=0;tray=0;' -and !(Test-Path -LiteralPath "$prefix.error") -and
           !(Test-Path -LiteralPath "$prefix.gesture-stopped") -and !(Test-Path -LiteralPath "$prefix.clipboard-stopped")) {
            Write-Output '演示进程已退出；观察探针的空手势服务误报，hotkeys=0;tray=0;clipboardService=0'
        } else { throw }
    }
}
try {
    foreach($key in $names){$saved[$key]=[Environment]::GetEnvironmentVariable($key)}
    $env:PANE_THEME='Dark';$env:PANE_PSEUDO_LOC='1';$env:PANE_VERIFY=$null;$env:PANE_OBSERVE='1'
    $env:PANE_WALLPAPER=([Uri](Join-Path $repo 'prototype/wallpapers/shapes.svg')).AbsoluteUri
    $env:PANE_DESIGN_UPDATE='ready'
    foreach($case in $cases) {
        if($Only -and $case.Name -notlike $Only){continue}
        $env:PANE_DESIGN_QUERY=$case.Query;$env:PANE_DESIGN_SETTINGS=$case.Section;$env:PANE_DESIGN_OPEN=$case.Open;$env:PANE_DESIGN_SELECTION=$case.Selection
        $prefix=New-Result ('049-capture-'+$case.Name);$process=$null
        try {
            $process=Start-Pane $Mode $prefix $true;Wait-Ready $process $prefix
            if($case.Command){Send-Control $process $prefix ('show-'+$case.Command) ('shown-'+$case.Command)}
            Start-Sleep -Milliseconds 1000
            $windows=@([Observe009]::Windows($process.Id)|Where-Object {[Observe009]::IsWindowVisible($_)})
            $back=@($windows|Where-Object {[Observe009]::Title($_)-eq 'Pane 011 实验背景'})
            if($back.Count-ne 1){throw '未找到独立截图背景'};$back=$back[0]
            $plain=@($windows|Where-Object {$_ -ne $back -and ([Observe009]::GetWindowLong($_,-20)-band 0x80000)-eq 0 -and [Observe009]::Title($_).Contains($case.Title)})
            if($plain.Count-ne 1){throw "无法唯一找到 $($case.Name) 的内容窗"}
            # 只置序本次演示实例的窗，保持原有双 HWND 的视觉窗/内容窗相邻关系。
            foreach($w in $windows|Where-Object {$_-ne $back -and ([Observe009]::GetWindowLong($_,-20)-band 0x80000)-ne 0}){[void][Z]::SetWindowPos($w,[IntPtr](-1),0,0,0,0,0x13)}
            foreach($w in $plain){[void][Z]::SetWindowPos($w,[IntPtr](-1),0,0,0,0,0x13);$owner=[Z]::GetWindow($w,4);if($owner-ne [IntPtr]::Zero){[void][Z]::SetWindowPos($owner,$w,0,0,0,0,0x213)}}
            [void][Z]::DwmFlush();Start-Sleep -Milliseconds 150
            Save-Shot $plain[0] $back $process.Id ('pseudo-dark-'+$case.Name)
        } finally {Stop-Demo $process $prefix}
    }
} finally {foreach($key in $names){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}
