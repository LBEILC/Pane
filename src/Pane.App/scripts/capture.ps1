param([ValidateSet('jit','aot')][string]$Mode='aot',[ValidateSet('Light','Dark','System')][string]$Theme='Dark',[ValidateSet('launcher','clipboard','super','settings','glass','resize','cycle','positions','drag','pointer','gesture','all')][string]$Shot='all',[switch]$NoShot,[ValidateSet('gdi','dxgi','both')][string]$CaptureBackend='gdi')
. "$PSScriptRoot/common.ps1"
. "$PSScriptRoot/capture-native.ps1"
. "$PSScriptRoot/capture-functions.ps1"
Add-Type -AssemblyName System.Drawing
if(!$NoShot -and $CaptureBackend -ne 'gdi'){. "$PSScriptRoot/capture-dxgi.ps1"}
$repo=Split-Path (Split-Path $root -Parent) -Parent
$out=Join-Path $repo 'docs/research/refs/011';New-Item -ItemType Directory -Force $out|Out-Null
$name="$Mode-$Theme";$prefix=Join-Path $out ($name+'-'+[Guid]::NewGuid().ToString('N'));$process=$null
$settings=@{PANE_THEME=$Theme;PANE_WALLPAPER=$(if($NoShot){''}else{([Uri](Join-Path $repo 'prototype/wallpapers/shapes.svg')).AbsoluteUri});PANE_CYCLE_CAPTURE=([int](!$NoShot)).ToString()};$old=@{}
function Send-Demo([string]$command,[string]$ack,[string]$text=''){Send-Control $process $prefix $command $ack $text 90}
function Panel-Window([string]$title) {
    $found=@([Observe009]::Windows($process.Id)|Where-Object {[Observe009]::Title($_)-eq $title -and [Observe009]::IsWindowVisible($_)})
    # 双 HWND 宿主中，非分层窗口是内容边界；它与面板的可见圆角一致。
    $found=@($found|Where-Object {([Observe009]::GetWindowLong($_,-20)-band 0x80000)-eq 0})
    if($found.Count-ne 1){throw "无法按 PID 唯一找到 $title 内容窗口"}
    $found[0]
}
# 先排好内容窗与视觉窗，再把背景放到最低视觉窗下，避免背景短暂盖住材质。
# 0x213 = SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE|SWP_NOOWNERZORDER。
function Raise-All {
    $mine=@([Observe009]::Windows($process.Id)|Where-Object {$_-ne $back -and [Observe009]::IsWindowVisible($_)})
    $layered=@($mine|Where-Object {([Observe009]::GetWindowLong($_,-20)-band 0x80000)-ne 0})
    $plain=@($mine|Where-Object {([Observe009]::GetWindowLong($_,-20)-band 0x80000)-eq 0})
    foreach($w in $layered+$plain){[void][Z]::SetWindowPos($w,[IntPtr](-1),0,0,0,0,0x13)}
    foreach($w in $plain){
        $owner=[Z]::GetWindow($w,4)
        if($owner -in $layered){
            if(![Z]::SetWindowPos($owner,$w,0,0,0,0,0x213)){throw '无法将视觉窗放到内容窗正下方'}
        }
    }
    if($layered.Count){
        if(![Z]::SetWindowPos($back,$layered[0],0,0,0,0,0x213)){throw '无法将背景放到视觉窗下方'}
    }
}
function Capture-Panel([string]$title,[string]$suffix) {
    if($NoShot){return}
    $win=Panel-Window $title
    $windows=@([Observe009]::Windows($process.Id)|ForEach-Object {
        $r=New-Object Observe009+Rect;[void][Observe009]::GetWindowRect($_,[ref]$r)
        "hwnd=$_;title=$([Observe009]::Title($_));visible=$([Observe009]::IsWindowVisible($_));ex=$([Observe009]::GetWindowLong($_,-20));above=$([Z]::GetWindow($_,3));below=$([Z]::GetWindow($_,2));owner=$([Z]::GetWindow($_,4));rect=$($r.Left),$($r.Top),$($r.Right),$($r.Bottom)"
    })
    $windows | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $out "$name-$suffix-hosts.txt")
    Raise-All
    # 置序改变了 HostBackdrop 的采样背景，等待 DWM 呈现后再取图。
    [void][Z]::DwmFlush()
    Start-Sleep -Milliseconds 150
    Save-Shot $win $back $process.Id "$name-$suffix"
}

try {
    foreach($key in $settings.Keys){$old[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$settings[$key])}
    $process=Start-Pane $Mode $prefix $true;Wait-Ready $process $prefix
    $bounds=New-Object Observe009+Rect
    if(!$NoShot){
        $backs=@([Observe009]::Windows($process.Id)|Where-Object {[Observe009]::Title($_)-eq 'Pane 011 实验背景'})
        if($backs.Count-ne 1){throw '未找到背景夹具'};$back=$backs[0]
        [void][Observe009]::GetWindowRect($back,[ref]$bounds)
    }else{$bounds.Right=800;$bounds.Bottom=600}
    Send-Demo 'theme' 'theme-applied' $Theme.ToLowerInvariant()
    $saved=Get-Content -LiteralPath "$prefix-data/settings.json" -Raw | ConvertFrom-Json
    $effectiveTheme=if(([IO.File]::ReadAllText("$prefix.ready"))-match 'theme=Dark'){'Dark'}else{'Light'}
    $defaults=if($effectiveTheme-eq 'Dark'){$saved.General.GlassDark}else{$saved.General.GlassLight}
    $titles=@{launcher='启动器';clipboard='剪贴板';super='超级面板';settings='设置'}
    if($Shot-in @('all','drag')){
        Send-Demo 'show-launcher' 'shown-launcher'
        Send-Demo 'drag' 'dragged' '300,200'
        Send-Demo 'hide' 'hidden'
        Send-Demo 'show-launcher' 'shown-launcher'
        Capture-Panel '启动器' 'drag-restored'
        Send-Demo 'reset-position' 'position-reset'
        Capture-Panel '启动器' 'drag-reset'
        Send-Demo 'hide' 'hidden'
    }
    if($Shot-in @('all','pointer')){
        Send-Demo 'show-clipboard' 'shown-clipboard'
        Send-Demo 'show-at-pointer' 'shown-at-pointer' "$([int](($bounds.Left+$bounds.Right)/2)),$([int](($bounds.Top+$bounds.Bottom)/2))"
        Capture-Panel '剪贴板' 'pointer-center'
        Send-Demo 'show-at-pointer' 'shown-at-pointer' "$($bounds.Right-40),$($bounds.Bottom-40)"
        Capture-Panel '剪贴板' 'pointer-bottom-right'
        Send-Demo 'hide' 'hidden'
    }
    if($Shot-in @('all','gesture')){
        # 设计：手势轨迹 + 提示框。提示框和轨迹窗都是分层穿透窗，按背景中心截一块固定区域。
        $cx=[int](($bounds.Left+$bounds.Right)/2);$cy=[int](($bounds.Top+$bounds.Bottom)/2)
        $cases=[ordered]@{
            'match'='Drawing;4,2;None;关闭标签页;-240,-190 -240,0 20,0'
            'unknown'='Drawing;4,6,4,6;None;;-120,-200 -120,-110 -230,-110 -230,-20 -340,-20'
            'modifier'='Drawing;0;WheelUp;放大;-260,40 -260,-160'
            'long'='Drawing;4,2,0,2,4;None;撤销;-400,-200 -400,-80 -280,-80 -280,-200 -160,-200 -160,-80'
            'executed'='Executed;2,4,2;None;任务选择;-300,-120 -180,-120 -180,0 -60,0'
            'notice'='Notice;;None;手势已暂停;0,0'
            'failed'='Failed;;None;粘贴;;没有权限往这个窗口输入（它可能以管理员身份运行）'
        }
        foreach($case in $cases.Keys){
            Send-Demo 'gesture' 'gesture-shown' $cases[$case]
            if(!$NoShot){
                [IO.File]::WriteAllText((Join-Path $out "$name-gesture-$case-hosts.txt"),'')
                Raise-All
                # 提示框在轨迹上面（原型 z-index 60 > 50）：先视觉窗、再它拥有的内容窗
                $hint=@([Observe009]::Windows($process.Id)|Where-Object {[Observe009]::Title($_)-eq '手势提示' -and [Observe009]::IsWindowVisible($_)})
                foreach($w in @($hint|Where-Object {[Z]::GetWindow($_,4)-eq [IntPtr]::Zero})+@($hint|Where-Object {[Z]::GetWindow($_,4)-ne [IntPtr]::Zero})){[void][Z]::SetWindowPos($w,[IntPtr](-1),0,0,0,0,0x13)}
                [void][Z]::DwmFlush();Start-Sleep -Milliseconds 150
                Save-Region $back $process.Id "$name-gesture-$case" ($cx-520) ($cy-280) 1040 560
            }
        }
        Send-Demo 'hide' 'hidden'
    }
    foreach($panel in @('launcher','clipboard','super','settings')){
        if($Shot-in @('all',$panel)){$point=if($panel-eq 'super'){"$($bounds.Right-40),$($bounds.Bottom-40)"}else{''};Send-Demo "show-$panel" "shown-$panel" $point;Capture-Panel $titles[$panel] $panel;Send-Demo 'hide' 'hidden'}
    }
    if($Shot-in @('all','glass')){
        Send-Demo 'show-launcher' 'shown-launcher'
        foreach($variant in @('default','tint0','tint1','highlight0','highlight1','blur0','blur20')){
            $g=@{};foreach($property in $defaults.PSObject.Properties){$g[$property.Name]=$property.Value}
            switch($variant){tint0{$g.TintOpacity=0} tint1{$g.TintOpacity=1} highlight0{$g.Highlight=0} highlight1{$g.Highlight=1} blur20{$g.ExtraBlur=20}}
            $activeTheme=$Theme
            Send-Demo 'glass' 'glass-applied' (@{Theme=$activeTheme;Parameters=$g}|ConvertTo-Json -Compress)
            Capture-Panel '启动器' "glass-$variant"
        }
        Send-Demo 'hide' 'hidden'
    }
    if($Shot-in @('all','resize')){Send-Demo 'show-launcher' 'shown-launcher';Send-Demo 'radius' 'radius-applied' '28';Send-Demo 'resize' 'resized' '784,520';Capture-Panel '启动器' 'resize';Send-Demo 'radius' 'radius-applied' '20';Send-Demo 'hide' 'hidden'}
    if($Shot-in @('all','cycle')){
        if($NoShot){Send-Demo 'cycle' 'cycled'}else{
            [IO.File]::WriteAllText("$prefix.cycle",'start')
            for($i=1;$i-le 60;$i++){Wait-Ready $process $prefix "cycle-$i";Capture-Panel $titles[@('launcher','clipboard','super')[($i-1)%3]] "cycle-$i";[IO.File]::WriteAllText("$prefix.cycle-next",'next')}
            Wait-Ready $process $prefix 'cycled'
        }
        $lines=@(Get-Content -LiteralPath "$prefix.cycle-diagnostics")
        if($lines.Count-ne 60-or @($lines|Where-Object {$_-notmatch 'material=True;clip=True;shadow=True;visible=True'}).Count){throw '60 次合成树诊断未通过'}
    }
    if($Shot -in @('all','positions')){
        [IO.File]::WriteAllText("$prefix.positions",'start')
        for($i=1;$i-le 18;$i++){
            Wait-Ready $process $prefix "position-$i"
            $panel=if($i-le 9){'clipboard'}else{'super'}
            Capture-Panel $titles[$panel] "positions-$panel-$(($i-1)%9+1)"
            if(!$NoShot){[IO.File]::WriteAllText("$prefix.position-next",'next')}
        }
        Wait-Ready $process $prefix 'positioned'
    }
} finally {try {Stop-Pane $process $prefix} finally {foreach($key in $old.Keys){[Environment]::SetEnvironmentVariable($key,$old[$key])}}}
Write-Host "演示完成：$prefix"
