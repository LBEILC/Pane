$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot -Parent
$repo=Split-Path (Split-Path $root -Parent) -Parent
[xml]$svg=Get-Content -LiteralPath "$root/Assets/pane-app.svg" -Raw
# 64 / 256：任务栏、Alt+Tab 和资源管理器在高缩放下用（256 在 ICO 目录里写成 0）
$sizes=@(16,20,24,32,40,48,64,256);$images=@()
foreach($size in $sizes){
    $bitmap=New-Object Drawing.Bitmap($size,$size);$g=[Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode='AntiAlias';$g.ScaleTransform($size/100.0,$size/100.0)
    foreach($shape in $svg.svg.path){
        $path=New-Object Drawing.Drawing2D.GraphicsPath
        $tokens=[regex]::Matches($shape.d,'[MLCZ]|-?\d+(?:\.\d+)?')|ForEach-Object {$_.Value}
        $i=0;[single]$x=0;[single]$y=0
        while($i-lt $tokens.Count){
            $op=$tokens[$i++];switch($op){
                'M' {$x=[single]::Parse($tokens[$i++],[Globalization.CultureInfo]::InvariantCulture);$y=[single]::Parse($tokens[$i++],[Globalization.CultureInfo]::InvariantCulture);$path.StartFigure()}
                'L' {$nx=[single]::Parse($tokens[$i++],[Globalization.CultureInfo]::InvariantCulture);$ny=[single]::Parse($tokens[$i++],[Globalization.CultureInfo]::InvariantCulture);$path.AddLine($x,$y,$nx,$ny);$x=$nx;$y=$ny}
                'C' {$v=@();for($j=0;$j-lt 6;$j++){$v+=[single]::Parse($tokens[$i++],[Globalization.CultureInfo]::InvariantCulture)};$path.AddBezier($x,$y,$v[0],$v[1],$v[2],$v[3],$v[4],$v[5]);$x=$v[4];$y=$v[5]}
                'Z' {$path.CloseFigure()}
                default {throw "不支持 SVG 指令 $op"}
            }
        }
        $brush=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml($shape.fill));$g.FillPath($brush,$path);$brush.Dispose();$path.Dispose()
    }
    $stream=New-Object IO.MemoryStream;$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png);$images+=,@($stream.ToArray());$stream.Dispose();$g.Dispose();$bitmap.Dispose()
}
$file=[IO.File]::Create("$root/Assets/pane.ico");$writer=New-Object IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count);$offset=6+16*$sizes.Count
    for($i=0;$i-lt $sizes.Count;$i++){$writer.Write([byte]($sizes[$i]%256));$writer.Write([byte]($sizes[$i]%256));$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset);$offset+=$images[$i].Length}
    foreach($image in $images){$writer.Write([byte[]]$image)}
} finally {$writer.Dispose()}
