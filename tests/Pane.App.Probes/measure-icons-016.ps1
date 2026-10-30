param([string]$DataDirectory,[int]$RunCount=3)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
Add-Type -AssemblyName System.Drawing
$out=Join-Path $repo 'docs/research/refs/016'
New-Item -ItemType Directory -Force $out | Out-Null
if(!$DataDirectory){$DataDirectory=(New-Result '016-icons')+'-data'}
$values=@{PANE_OBSERVE='1';PANE_VERIFY='1';PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'}
$saved=@{};$runs=@()
try {
 foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
 for($i=1;$i-le $RunCount;$i++){
  $prefix=New-Result '016-idle';$process=$null
  try {
   $process=Start-Pane 'aot' $prefix $false $DataDirectory;Wait-Ready $process $prefix
   Send-Control $process $prefix 'launcher-index' 'launcher-index-result' '' 300
   $index=[IO.File]::ReadAllText("$prefix.launcher-index-result")
   $child=[IO.File]::ReadAllText("$prefix.launcher-process-result")
   if($index-notmatch '^total=[1-9]' -or $child-notmatch 'childOutcome=completed;'){throw "索引失败：$index;$child"}
   $childId=[int]([regex]::Match($child,'childPid=(\d+)').Groups[1].Value)
   if(Get-Process -Id $childId -ErrorAction SilentlyContinue){throw '辅助进程未退出'}
   $metrics=Get-Content -Raw -Encoding UTF8 (Join-Path $DataDirectory 'icon-measurements.json') | ConvertFrom-Json
   if(@($metrics | Where-Object Error).Count){throw '图标处理失败，见 icon-measurements.json'}
   $available=@($metrics | Where-Object Original)
   Copy-Item -LiteralPath (Join-Path $DataDirectory 'icon-measurements.json') -Destination (Join-Path $out "run-$i-icons.json")
   Copy-Item -LiteralPath (Join-Path $DataDirectory 'catalog-evidence.json') -Destination (Join-Path $out 'catalog-evidence.json')
   $catalogRows=Get-Content -Raw -Encoding UTF8 (Join-Path $DataDirectory 'catalog-evidence.json')|ConvertFrom-Json
   $snapshot=Get-Content -Raw -Encoding UTF8 (Join-Path $DataDirectory 'launcher-index.json')|ConvertFrom-Json
   if(@($catalogRows|Group-Object Key|Where-Object Count -gt 1).Count){throw '仍有重复目标'}
   if(@($snapshot.Snapshot.Apps|Group-Object Id|Where-Object Count -gt 1).Count){throw '应用 Id 冲突'}
   foreach($name in @('控制面板','事件查看器','性能监视器','资源监视器','任务计划程序','任务管理器','计算机管理','运行')){
    if(@($catalogRows|Where-Object Name -eq $name).Count-ne 1){throw "系统工具名称或条数不符：$name"}
   }
   $misses=@($available | Where-Object {!$_.Cached}).Count
   if($i-gt 1 -and $misses-ne 0){throw "二次启动重算了 $misses 个图标"}
   Start-Sleep -Seconds 10;$process.Refresh()
   $times=@($available.Milliseconds | Sort-Object)
   $runs+=[pscustomobject]@{Run=$i;WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB;Index=$index;Child=$child;CacheMisses=$misses;CacheHits=$available.Count-$misses;IconMedianMs=$times[[int][Math]::Floor($times.Count/2)];IconMaxMs=$times[-1];IconTotalMs=($times|Measure-Object -Sum).Sum;Prefix=$prefix}
   Write-Host ($runs[-1]|ConvertTo-Json -Compress)
   if($i-eq 1){Copy-Item -LiteralPath (Join-Path $DataDirectory 'icon-measurements.json') -Destination (Join-Path $out 'first-measurements.json')}
  } finally {Stop-Pane $process $prefix}
 }
 $median=@($runs.WorkingMiB|Sort-Object)[[int][Math]::Floor($runs.Count/2)]
 [pscustomobject]@{Runs=$runs;MedianWorkingMiB=$median;DataDirectory=$DataDirectory;AllProcessesStopped=$true}|ConvertTo-Json -Depth 6|Set-Content -Encoding UTF8 (Join-Path $out 'measurements.json')
 if($median-gt 150){throw "空闲工作集超过目标：$median MiB"}
} finally {foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}

# 只生成验收图，不启动本机应用；按 64px 显示每个图标。
$items=Get-Content -Raw -Encoding UTF8 (Join-Path $DataDirectory 'icon-measurements.json')|ConvertFrom-Json
$font=New-Object Drawing.Font('Microsoft YaHei',10)
$brush=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(230,230,235))
$darkBrush=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#1c1c20'))
$lightBrush=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#f6f6f8'))
$format=New-Object Drawing.StringFormat
$format.Trimming=[Drawing.StringTrimming]::EllipsisCharacter
$labels=@{Full='满幅';Plate='彩色底板';Neutral='中性底板';Raw='原样';Empty='透明';Unavailable='无原始图标'}
try {
 $catalog=Get-Content -Raw -Encoding UTF8 (Join-Path $DataDirectory 'catalog-evidence.json')|ConvertFrom-Json
 $smallNames=@($catalog|Where-Object {$_.Direct -and [Math]::Max($_.Width,$_.Height)-lt 64}|ForEach-Object Name)
 $small=@($items|Where-Object {$_.Name -in $smallNames -or $_.Name -match '必剪|微信输入法|放大镜|王国|Kingdom|少女前线|循环勇士|Loop Hero|Operation.*Tango|雨中冒险|Risk of Rain|Palworld|Balatro|Gunman|王之凝视|逃离鸭科夫|PEAK|龟龟'})
 $pages=[int][Math]::Ceiling($items.Count/40)
 for($page=0;$page-le $pages;$page++){
  $rows=if($page-eq $pages){$small}else{@($items|Select-Object -Skip ($page*40) -First 40)}
  $bitmap=New-Object Drawing.Bitmap(660,(50+$rows.Count*80));$graphics=[Drawing.Graphics]::FromImage($bitmap)
  try {
   $graphics.Clear([Drawing.Color]::FromArgb(40,40,45));$graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $graphics.DrawString('应用 / 原始图标 / 判定 / 深色 / 浅色',$font,$brush,12,12)
   for($j=0;$j-lt $rows.Count;$j++){
    $row=$rows[$j];$y=50+$j*80
    $graphics.DrawString($row.Name,$font,$brush,(New-Object Drawing.RectangleF(12,$y,200,70)),$format)
    $graphics.DrawString($labels[$row.Kind],$font,$brush,310,($y+24))
    $graphics.FillRectangle($darkBrush,430,$y,80,80);$graphics.FillRectangle($lightBrush,540,$y,80,80)
    foreach($pair in @(@($row.Original,224),@($row.Dark,438),@($row.Light,548))){
     if($pair[0] -and (Test-Path -LiteralPath $pair[0])){$image=[Drawing.Image]::FromFile($pair[0]);try{$graphics.DrawImage($image,[int]$pair[1],($y+8),64,64)}finally{$image.Dispose()}}
    }
   }
   $name=if($page-eq $pages){'contact-sheet-small.png'}elseif($page-eq 0){'contact-sheet.png'}else{"contact-sheet-$($page+1).png"}
   $bitmap.Save((Join-Path $out $name),[Drawing.Imaging.ImageFormat]::Png)
  } finally {$graphics.Dispose();$bitmap.Dispose()}
 }
} finally {$font.Dispose();$brush.Dispose();$darkBrush.Dispose();$lightBrush.Dispose();$format.Dispose()}
Write-Host "对照图已生成：$out；所有测试进程已退出。"
