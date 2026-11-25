# 导出与打包共享政策读取、解码及示意路径豁免；规则本身不进入公开源码。
function Read-ForbiddenRules([string]$PolicyPath) {
    $policy=[IO.File]::ReadAllText($PolicyPath)
    $block=[regex]::Match($policy,'(?ms)^```forbidden\s*\r?\n(.*?)^```')
    if(!$block.Success){throw '缺少禁用词规则'}
    $result=@($block.Groups[1].Value -split '\r?\n' | Where-Object {$_.Trim() -ne ''} | ForEach-Object {[regex]::new($_,[Text.RegularExpressions.RegexOptions]::IgnoreCase)})
    if(!$result.Count){throw '禁用词规则为空'}
    return $result
}
function Mask-Example([string]$Value) {
    $latinName=[Text.Encoding]::GetEncoding(28591).GetString([Text.Encoding]::UTF8.GetBytes('你'))
    $pattern='(?i)[A-Z]:[\\/]+Users[\\/]+(?:你|'+[regex]::Escape($latinName)+')(?=[\\/]|$)'
    [regex]::Replace($Value,$pattern,[Text.RegularExpressions.MatchEvaluator]{param($m) ' ' * $m.Length})
}
$ForbiddenEncodings=@([Text.Encoding]::UTF8,[Text.Encoding]::GetEncoding(28591),[Text.Encoding]::Unicode)
function Assert-ForbiddenFiles([string[]]$Files,[regex[]]$Rules) {
    $hits=[Collections.Generic.List[string]]::new()
    foreach($file in $Files){
        $bytes=[IO.File]::ReadAllBytes($file)
        foreach($encoding in $ForbiddenEncodings){
            $content=Mask-Example ($encoding.GetString($bytes))
            for($i=0;$i -lt $Rules.Count;$i++){
                if($Rules[$i].IsMatch($content)){$hits.Add("$file : 规则 $($i+1)，$($encoding.WebName)")}
            }
        }
    }
    if($hits.Count){$hits | Write-Host;throw "打包扫描失败：$($hits.Count) 个命中（含不同解码）"}
    Write-Host "打包扫描通过：$($Files.Count) 个文件，$($Rules.Count) 条规则，UTF-8 / Latin-1 / UTF-16LE，0 命中"
}

# 先检查 ZIP 条目名和展开的清单，再用客户端的还原与全目录哈希校验检查最终内容。
function Assert-ReleasePatch([string]$Patch,[string]$Baseline,[string]$Manifest,[string]$From,[string]$To,[regex[]]$Rules) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $work=New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('Pane-Scan-'+[guid]::NewGuid()))
    try {
        $zip=[IO.Compression.ZipFile]::OpenRead($Patch)
        try {
            $names=Join-Path $work.FullName 'names.txt'
            [IO.File]::WriteAllLines($names,[string[]]@($zip.Entries | ForEach-Object {$_.FullName}))
            $metadata=@($names)
            foreach($entry in $zip.Entries){
                if($entry.FullName -cmatch '^[df]/[0-9]+$'){continue}
                if($entry.FullName -cnotin 'base.json','patch.json'){throw "未知补丁条目：$($entry.FullName)"}
                $path=Join-Path $work.FullName $entry.FullName
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$path)
                $metadata+=$path
            }
            Assert-ForbiddenFiles -Files $metadata -Rules $Rules
        } finally {$zip.Dispose()}
        $output=Join-Path $work.FullName 'verified'
        $tool=Join-Path $PSScriptRoot 'delta/bin/Release/net10.0-windows/Pane.Delta.dll'
        & dotnet @($tool,'verify',$Baseline,$Patch,$Manifest,$From,$To,$output)
        if($LASTEXITCODE){throw "补丁还原校验失败：$From 至 $To"}
        Assert-ForbiddenFiles -Files @(Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {$_.FullName}) -Rules $Rules
    } finally {
        . (Join-Path $PSScriptRoot 'remove-tree.ps1')
        Remove-LinkSafeTree $work.FullName
    }
}
