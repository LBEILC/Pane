param([string]$Source,[string]$Output)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
if(!$Output){$Output=Join-Path $PSScriptRoot '../../src/Pane.App/Assets/Engines'}
$Output=[IO.Path]::GetFullPath($Output)
if($Source){
    New-Item -ItemType Directory -Force -Path $Output | Out-Null
    foreach($pair in @(@('google','google-g-128.png'),@('bing','bing.ico'),@('bilibili','bilibili.ico'),@('github','github.ico'),@('baidu','si-baidu.svg'))){
        $file=Join-Path $Source $pair[1]
        if($pair[0] -eq 'baidu'){
            [xml]$svg=Get-Content -LiteralPath $file -Raw -Encoding UTF8
            $geometry=[Windows.Media.Geometry]::Parse($svg.svg.path.d)
            $visual=[Windows.Media.DrawingVisual]::new();$drawing=$visual.RenderOpen()
            $drawing.PushTransform([Windows.Media.ScaleTransform]::new(128/24,128/24))
            $drawing.DrawGeometry([Windows.Media.Brushes]::Black,$null,$geometry);$drawing.Pop();$drawing.Close()
            $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(128,128,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($visual)
        }else{
            $stream=[IO.File]::OpenRead([IO.Path]::GetFullPath($file))
            try{$decoder=[Windows.Media.Imaging.BitmapDecoder]::Create($stream,[Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,[Windows.Media.Imaging.BitmapCacheOption]::OnLoad);$bitmap=$decoder.Frames | Sort-Object { $_.PixelWidth*$_.PixelHeight } -Descending | Select-Object -First 1}finally{$stream.Dispose()}
            if([Math]::Max($bitmap.PixelWidth,$bitmap.PixelHeight) -gt 128){$scale=128/[Math]::Max($bitmap.PixelWidth,$bitmap.PixelHeight);$bitmap=[Windows.Media.Imaging.TransformedBitmap]::new($bitmap,[Windows.Media.ScaleTransform]::new($scale,$scale))}
        }
        $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream=[IO.File]::Create((Join-Path $Output ($pair[0]+'.png')));try{$encoder.Save($stream)}finally{$stream.Dispose()}
    }
}
foreach($name in @('google','bing','bilibili','github','baidu')){
    $stream=[IO.File]::OpenRead((Join-Path $Output ($name+'.png')))
    try{$frame=[Windows.Media.Imaging.BitmapDecoder]::Create($stream,[Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,[Windows.Media.Imaging.BitmapCacheOption]::OnLoad).Frames[0]}finally{$stream.Dispose()}
    $rgba=[Windows.Media.Imaging.FormatConvertedBitmap]::new($frame,[Windows.Media.PixelFormats]::Bgra32,$null,0)
    $pixels=New-Object byte[] ($rgba.PixelWidth*$rgba.PixelHeight*4);$rgba.CopyPixels($pixels,$rgba.PixelWidth*4,0)
    if($name -eq 'bing'){
        # 旧来源是蓝色放大镜；比较解码后的像素，避免重新编码 PNG 后漏检。
        $sha=[System.Security.Cryptography.SHA256]::Create()
        try{$pixelHash=[BitConverter]::ToString($sha.ComputeHash($pixels)).Replace('-','')}finally{$sha.Dispose()}
        if($pixelHash -eq 'B6A573D2C22C895FD83ECA5C2D6CB8A0165E7B8C1CDEF569524687A1617115BD'){throw '必应图标不能使用旧蓝色放大镜'}
        Write-Output 'bing differsFromMagnifier=True'
    }
    $transparent=0;$white=0;$corners=0
    for($i=0;$i -lt $pixels.Length;$i+=4){if($pixels[$i+3] -eq 0){$transparent++};if($pixels[$i+3] -eq 255 -and $pixels[$i] -ge 240 -and $pixels[$i+1] -ge 240 -and $pixels[$i+2] -ge 240){$white++}}
    foreach($i in @(0,($rgba.PixelWidth-1),(($rgba.PixelHeight-1)*$rgba.PixelWidth),($rgba.PixelWidth*$rgba.PixelHeight-1))){if($pixels[$i*4+3] -eq 0){$corners++}}
    if(!$transparent -or $white -or $corners -ne 4 -or [Math]::Max($rgba.PixelWidth,$rgba.PixelHeight) -gt 128){throw "透明或尺寸检查失败：$name"}
    Write-Output "$name size=$($rgba.PixelWidth)x$($rgba.PixelHeight) transparent=$transparent opaqueNearWhite=$white transparentCorners=$corners"
}
