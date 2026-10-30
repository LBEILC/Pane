param([ValidateSet('jit','aot','Both')][string]$Mode='Both')
. "$PSScriptRoot/common.ps1"
function Info {
    Send-Control $process $prefix 'position-info' 'position-info-result'
    $line=[IO.File]::ReadAllText("$prefix.position-info-result")
    $result=@{}
    foreach($part in $line.Split(';')){$pair=$part.Split('=',2);if($pair.Length-eq 2){$result[$pair[0]]=$pair[1]}}
    $result.rect=@($result.inputRect.Split(',')|ForEach-Object {[double]::Parse($_,[Globalization.CultureInfo]::InvariantCulture)})
    $result.workRect=@($result.work.Split(',')|ForEach-Object {[double]::Parse($_,[Globalization.CultureInfo]::InvariantCulture)})
    $result.dpi=[double]::Parse($result.dpi,[Globalization.CultureInfo]::InvariantCulture)
    return $result
}
function Assert-Inside($info){
    $r=$info.rect;$w=$info.workRect;$edge=[Math]::Ceiling(8*$info.dpi)
    if($r[0]-lt $w[0]+$edge-or $r[1]-lt $w[1]+$edge-or $r[2]-gt $w[2]-$edge-or $r[3]-gt $w[3]-$edge){throw "面板越界：$($info.inputRect)；工作区 $($info.work)"}
}
function Assert-Default($info){
    $r=$info.rect;$w=$info.workRect
    $x=$w[0]+($w[2]-$w[0]-($r[2]-$r[0]))/2
    # 启动器以 60 DIP 搜索行的上沿定位，结果随后向下展开。
    $y=$w[1]+($w[3]-$w[1]-60*$info.dpi)/4
    if([Math]::Abs($r[0]-$x)-gt 1-or [Math]::Abs($r[1]-$y)-gt 1){throw '没有回到上部居中默认位置'}
}
$modes=if($Mode-eq 'Both'){@('jit','aot')}else{@($Mode)}
foreach($kind in $modes){
    $prefix=New-Result "position-$kind";$data="$prefix-data";$process=$null
    try {
        $process=Start-Pane $kind $prefix $true $data;Wait-Ready $process $prefix
        Send-Control $process $prefix 'show-launcher' 'shown-launcher'
        $initial=Info;Assert-Default $initial
        Send-Control $process $prefix 'drag' 'dragged' '300,200'
        $moved=Info;Assert-Inside $moved
        $saved=[IO.File]::ReadAllText("$data/settings.json")|ConvertFrom-Json
        if(!$saved.Launcher.Position.MonitorId){throw '位置未保存'}
        $position=$saved.Launcher.Position|ConvertTo-Json -Compress
        Send-Control $process $prefix 'hide' 'hidden'
        Send-Control $process $prefix 'show-launcher' 'shown-launcher'
        if((Info).inputRect-ne $moved.inputRect){throw '再次呼出位置不一致'}
        # 长高只约束本次显示，缩回后仍使用原上沿，且不写回设置。
        $height=[int](($moved.workRect[3]-$moved.workRect[1])/$moved.dpi-16)
        Send-Control $process $prefix 'resize' 'resized' "640,$height"
        Assert-Inside (Info)
        if((([IO.File]::ReadAllText("$data/settings.json")|ConvertFrom-Json).Launcher.Position|ConvertTo-Json -Compress)-ne $position){throw '长高改写了位置'}
        $originalHeight=[int](($moved.rect[3]-$moved.rect[1])/$moved.dpi)
        Send-Control $process $prefix 'resize' 'resized' "640,$originalHeight"
        if((Info).inputRect-ne $moved.inputRect){throw '缩回后没有恢复原上沿'}
    }finally{Stop-Pane $process $prefix}
    $prefix=New-Result "position-reload-$kind"
    try {
        $process=Start-Pane $kind $prefix $true $data;Wait-Ready $process $prefix
        Send-Control $process $prefix 'show-launcher' 'shown-launcher'
        if((Info).inputRect-ne $moved.inputRect){throw '重启读回位置不一致'}
        Send-Control $process $prefix 'reset-position' 'position-reset'
        Assert-Default (Info)
        if(([IO.File]::ReadAllText("$data/settings.json")|ConvertFrom-Json).Launcher.Position){throw '恢复后位置没有清空'}
        Send-Control $process $prefix 'show-clipboard' 'shown-clipboard'
        $clip=Info;$w=$clip.workRect
        foreach($point in @("$([int](($w[0]+$w[2])/2)),$([int](($w[1]+$w[3])/2))","$([int]$w[2]-1),$([int]$w[3]-1)")){
            Send-Control $process $prefix 'show-at-pointer' 'shown-at-pointer' $point
            $clip=Info;Assert-Inside $clip
            if([Math]::Abs(($clip.rect[2]-$clip.rect[0])/$clip.dpi-372)-gt 1-or [Math]::Abs(($clip.rect[3]-$clip.rect[1])/$clip.dpi-640)-gt 1){throw '高面板尺寸被裁切'}
        }
    }finally{Stop-Pane $process $prefix}
    $saved.Launcher.Position.MonitorId='Pane-test-disconnected-monitor'
    [IO.File]::WriteAllText("$data/settings.json",($saved|ConvertTo-Json -Depth 20))
    $prefix=New-Result "position-missing-$kind"
    try {
        $process=Start-Pane $kind $prefix $true $data;Wait-Ready $process $prefix
        Send-Control $process $prefix 'show-launcher' 'shown-launcher';Assert-Default (Info)
        Send-Control $process $prefix 'save-settings' 'settings-saved'
        if(([IO.File]::ReadAllText("$data/settings.json")|ConvertFrom-Json).Launcher.Position.MonitorId-ne 'Pane-test-disconnected-monitor'){throw '失联显示器记忆被覆盖'}
    }finally{Stop-Pane $process $prefix}
    $saved.Launcher.PSObject.Properties.Remove('Position')
    [IO.File]::WriteAllText("$data/settings.json",($saved|ConvertTo-Json -Depth 20))
    $prefix=New-Result "position-legacy-$kind"
    try {
        $process=Start-Pane $kind $prefix $true $data;Wait-Ready $process $prefix
        Send-Control $process $prefix 'show-launcher' 'shown-launcher';Assert-Default (Info)
        if(Test-Path -LiteralPath "$prefix.settings-recovered"){throw '旧文件被误判损坏'}
        Send-Control $process $prefix 'save-settings' 'settings-saved'
        if(([IO.File]::ReadAllText("$data/settings.json")|ConvertFrom-Json).Launcher.Position){throw '旧文件位置没有默认成空'}
    }finally{Stop-Pane $process $prefix}
    Write-Host "$kind 位置验证通过；本轮所有进程已退出；数据目录 $data"
}
