function Intersects($a, $x, $y, $w, $h) { $a.Left -lt $x + $w -and $a.Right -gt $x -and $a.Top -lt $y + $h -and $a.Bottom -gt $y }
# 从背景夹具往上走一遍 Z 序：上面凡是别的进程的可见、未隐藏(cloak)且与截图区域相交的窗口，都算遮挡
function Find-Occluders([IntPtr]$back, [int]$procId, $x, $y, $w, $h) {
  $list = @(); $cur = [Z]::GetWindow($back, 3)   # GW_HWNDPREV
  while ($cur -ne [IntPtr]::Zero) {
    $wpid = 0; [void][Observe009]::GetWindowThreadProcessId($cur, [ref]$wpid)
    if ($wpid -ne $procId -and [Observe009]::IsWindowVisible($cur)) {
      $cloak = 0; [void][Observe009]::DwmInt($cur, 14, [ref]$cloak, 4)
      $r = New-Object Observe009+Rect; [void][Observe009]::GetWindowRect($cur, [ref]$r)
      $ex = [Observe009]::GetWindowLong($cur, -20)
      if ($cloak -eq 0 -and $r.Right -gt $r.Left -and (Intersects $r $x $y $w $h)) {
        # 照 008：分层 + 鼠标穿透的全屏叠加层（如远程工具的覆盖层）放行但记录；其余一律算遮挡
        if (($ex -band 0x80020) -eq 0x80020) { Write-Host "放行叠加层 pid=$wpid 类=$([Observe009]::Class($cur))" }
        else { $list += ("pid={0} 类={1} 矩形={2},{3},{4},{5}" -f $wpid, [Observe009]::Class($cur), $r.Left, $r.Top, $r.Right, $r.Bottom) }
      }
    }
    $cur = [Z]::GetWindow($cur, 3)
  }
  $list
}
function Save-Shot([IntPtr]$win, [IntPtr]$back, [int]$procId, [string]$name) {
  $r = New-Object Observe009+Rect
  if ([Observe009]::DwmRect($win, 9, [ref]$r, 16) -ne 0) { throw '读取可见边界失败' }
  $x = $r.Left - 40; $y = $r.Top - 40; $w = $r.Right - $r.Left + 80; $h = $r.Bottom - $r.Top + 80
  Save-Region $back $procId $name $x $y $w $h
}
function Save-Region([IntPtr]$back, [int]$procId, [string]$name, [int]$x, [int]$y, [int]$w, [int]$h) {
  $b = New-Object Observe009+Rect; [void][Observe009]::GetWindowRect($back, [ref]$b)
  # 靠边窗口的 40px 外扩可能越过物理屏幕；保留画布尺寸，屏幕外区域透明并记录。
  $sx=[Math]::Max($x,$b.Left);$sy=[Math]::Max($y,$b.Top)
  $sw=[Math]::Min($x+$w,$b.Right)-$sx;$sh=[Math]::Min($y+$h,$b.Bottom)-$sy
  if($sw-le 0-or $sh-le 0){throw '截图不在背景夹具内'}
  if($sw-ne $w-or $sh-ne $h){
    "画布=$x,$y,${w},${h}；可见屏幕=$sx,$sy,${sw},${sh}；屏幕外像素透明" | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $out "$name-bounds.txt")
  }
  $occ = @(Find-Occluders $back $procId $sx $sy $sw $sh)
  if ($occ.Count) { throw "$name：截图区域有其他程序的窗口（$($occ -join ', ')），放弃截图" }
  $snapshot=@();$cur=[Z]::GetWindow($back,3)
  while($cur-ne [IntPtr]::Zero){
    if([Observe009]::IsWindowVisible($cur)){
      $rect=New-Object Observe009+Rect;[void][Observe009]::GetWindowRect($cur,[ref]$rect)
      if(Intersects $rect $sx $sy $sw $sh){
        $affinity=0;$affinityRead=[Z]::GetWindowDisplayAffinity($cur,[ref]$affinity)
        $snapshot += "hwnd=$cur;class=$([Observe009]::Class($cur));ex=$([Observe009]::GetWindowLong($cur,-20));owner=$([Z]::GetWindow($cur,4));rect=$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom);affinityRead=$affinityRead;affinity=$affinity"
      }
    }
    $cur=[Z]::GetWindow($cur,3)
  }
  $snapshot | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $out "$name-windows.txt")
  # 同一完成确认期间不发下一步命令，两条路径共用裁剪和遮挡检查。
  $backends=if($CaptureBackend-eq 'both'){@('gdi','dxgi')}elseif($CaptureBackend){@($CaptureBackend)}else{@('gdi')}
  $baseName=$name
  foreach($backend in $backends){
  $name=if($CaptureBackend-eq 'gdi'){$baseName}else{"$baseName-$backend"}
  [void][Z]::DwmFlush()
  $capturedAt=[DateTime]::UtcNow.ToString('O')
  $bmp = New-Object Drawing.Bitmap($w, $h)
  try {
    $g = [Drawing.Graphics]::FromImage($bmp)
        try {
      $g.Clear([Drawing.Color]::Transparent)
      if($backend-eq 'dxgi'){
        $frame=[DesktopDxgi]::Capture($sx,$sy,$sw,$sh)
        try {$g.DrawImageUnscaled($frame,$sx-$x,$sy-$y)} finally {$frame.Dispose()}
      }else{$g.CopyFromScreen($sx,$sy,$sx-$x,$sy-$y,[Drawing.Size]::new($sw,$sh))}
    } finally {$g.Dispose()}
    "backend=$backend;utc=$capturedAt;region=$sx,$sy,$sw,$sh" | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $out "$name-capture.txt")
    $bmp.Save((Join-Path $out "$name.png"))
    $zoom = New-Object Drawing.Bitmap(960, 960)
    try {
      $zg = [Drawing.Graphics]::FromImage($zoom)
      try { $zg.InterpolationMode = 'NearestNeighbor'; $zg.PixelOffsetMode = 'Half'; $zg.DrawImage($bmp, [Drawing.Rectangle]::new(0, 0, 960, 960), [Drawing.Rectangle]::new(40, 40, 120, 120), [Drawing.GraphicsUnit]::Pixel) } finally { $zg.Dispose() }
      $zoom.Save((Join-Path $out "$name-tl-8x.png"))
      $zg = [Drawing.Graphics]::FromImage($zoom)
      try { $zg.InterpolationMode = 'NearestNeighbor'; $zg.PixelOffsetMode = 'Half'; $zg.DrawImage($bmp, [Drawing.Rectangle]::new(0, 0, 960, 960), [Drawing.Rectangle]::new($w-160, $h-160, 120, 120), [Drawing.GraphicsUnit]::Pixel) } finally { $zg.Dispose() }
      $zoom.Save((Join-Path $out "$name-br-8x.png"))
    } finally { $zoom.Dispose() }
    "$name 已截：$x,$y ${w}x$h"
  } finally { $bmp.Dispose() }
  }
}
