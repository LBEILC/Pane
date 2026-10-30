param([string]$Output,[string]$Message,[switch]$ScanOnly,[switch]$SkipTests)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$repo=Split-Path $PSScriptRoot -Parent
function Git([string]$Directory,[string[]]$Arguments) {
    $result=& git.exe @('-C',$Directory,'-c','core.quotepath=false') @Arguments
    if($LASTEXITCODE -ne 0){throw "git 失败：$($Arguments[0])"}
    return ($result -join "`n")
}
function Paths([string]$Directory,[string[]]$Arguments) {
    (Git $Directory $Arguments).Split([char]0) | Where-Object {$_ -ne ''}
}
function Assert-Path([string]$Root,[string]$Relative) {
    if([IO.Path]::IsPathRooted($Relative) -or $Relative.Split([char[]]'/\') -contains '..'){throw "不安全路径：$Relative"}
    $current=$Root
    foreach($part in $Relative.Split([char[]]'/\')) {
        $current=Join-Path $current $part
        if((Test-Path -LiteralPath $current) -and ((Get-Item -Force -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "拒绝符号链接：$Relative"}
    }
}
# 白名单和扫描规则的唯一政策来源：docs/internal.md。
$allow='^(src/|tests/|scripts/|public/docs/|README\.md$|README\.en\.md$|LICENSE$|LICENSE-EXCEPTION\.md$|THIRD_PARTY_NOTICES\.md$|\.gitignore$|\.gitattributes$)'
$ignored=@(Paths $repo @('ls-files','-z','-ci','--exclude-standard'))
$files=@{}
foreach($entry in (Paths $repo @('ls-files','--stage','-z'))) {
    if($entry -notmatch '^(\d+) [a-f0-9]+ (\d)\t(.+)$'){throw '无法解析 Git 索引'}
    $mode=$Matches[1];$stage=$Matches[2];$path=$Matches[3]
    if($path -notmatch $allow -or $ignored -contains $path){continue}
    if($mode -eq '120000' -or $mode -eq '160000'){throw "拒绝符号链接或子模块：$path"}
    if($stage -ne '0'){throw "未解决的合并：$path"}
    Assert-Path $repo $path
    $target=$path -replace '^public/docs/','docs/'
    if($files.ContainsKey($target)){throw "导出路径冲突：$target"}
    $files[$target]=$path
}
. (Join-Path $PSScriptRoot 'forbidden-scan.ps1')
$rules=@(Read-ForbiddenRules (Join-Path $repo 'docs/internal.md'))
$errors=[Collections.Generic.List[string]]::new()
$payload=@{}
$encodings=$ForbiddenEncodings
foreach($target in ($files.Keys | Sort-Object)) {
    $path=$files[$target];$full=Join-Path $repo $path
    $bytes=[IO.File]::ReadAllBytes($full)
    $payload[$target]=$bytes
    $texts=@($encodings | ForEach-Object {$_.GetString($bytes)})
    $scanTexts=@($texts | ForEach-Object {Mask-Example $_})
    for($i=0;$i -lt $rules.Count;$i++) {
        if($rules[$i].IsMatch((Mask-Example $path)) -or $rules[$i].IsMatch((Mask-Example $target))){$errors.Add("$path : 规则 $($i+1)，路径")}
        foreach($content in $scanTexts) {
            $match=$rules[$i].Match($content)
            if($match.Success){
                $line=($content.Substring(0,$match.Index) -split "`n").Count
                $position=if($content.Contains([string][char]0)){'二进制'}else{"行 $line"}
                $errors.Add("$path : 规则 $($i+1)，$position")
            }
        }
    }
    if($path -notmatch '\.md$'){continue}
    $markdown=$texts[0]
    # 检查行内、图片、引用式链接和 HTML href/src；锚点与外部协议不属于本地文件。
    $links=[regex]::Matches($markdown,'!?\[[^\]\r\n]*\]\(\s*(?<url><[^>]+>|[^\s)]+)|(?m)^\s*\[[^\]]+\]:\s*(?<url><[^>]+>|\S+)|(?:href|src)=["''](?<url>[^"'']+)')
    foreach($link in $links) {
        $url=$link.Groups['url'].Value.Trim('<','>')
        if($url -match '^(#|[a-z][a-z0-9+.-]*:|//)'){continue}
        $url=[Uri]::UnescapeDataString(($url -split '[?#]',2)[0])
        if(!$url){continue}
        $base=Split-Path (Join-Path $repo $target) -Parent
        $resolved=[IO.Path]::GetFullPath((Join-Path $base $url))
        $prefix=$repo.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
        $relative=if($resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){$resolved.Substring($prefix.Length).Replace('\','/')}else{''}
        if(!$relative -or !$files.ContainsKey($relative)){$errors.Add("$path : 导出集合外链接 $url")}
    }
}
if($errors.Count){$errors | Sort-Object -Unique | Write-Host;throw "扫描失败：$($errors.Count) 个命中（含不同解码）"}
Write-Host "扫描通过：$($files.Count) 个文件"
if($ScanOnly){return}
if(!$Output){throw '必须指定 -Output'}
$changed=@(Paths $repo @('diff','HEAD','--name-only','-z'))
foreach($path in $changed){if($path -match $allow -and $ignored -notcontains $path){throw "导出文件工作区不干净：$path"}}
$destination=[IO.Path]::GetFullPath($Output)
if($destination -eq $repo -or $repo.StartsWith($destination.TrimEnd('\','/')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '输出目录不能是源仓库或其父目录'}
# 输出的每一级父目录都不能通过链接跳到别处。
$ancestor=$destination
while($ancestor){if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -Force -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '输出目录包含符号链接'};$ancestor=Split-Path $ancestor -Parent}
$existing=(Test-Path -LiteralPath $destination) -and @(Get-ChildItem -Force -LiteralPath $destination).Count -gt 0
$old=@()
if($existing){
    Assert-Path $destination '.git'
    if(!(Test-Path -LiteralPath (Join-Path $destination '.git') -PathType Container)){throw '非空目录没有公开副本标记'}
    if((Git $destination @('config','--local','--get','pane.publicExport')) -ne '1'){throw '没有公开副本标记'}
    if(!$Message){throw '增量导出必须提供 -Message'}
    if(Git $destination @('status','--porcelain','--untracked-files=no')){throw '公开副本存在未提交修改'}
    foreach($path in (Paths $destination @('ls-files','--others','-z'))){throw "公开副本有未跟踪文件（含忽略项）：$path"}
    $old=@(Paths $destination @('ls-files','-z'))
    foreach($entry in (Paths $destination @('ls-files','--stage','-z'))){if($entry -match '^(120000|160000) '){throw '公开副本包含符号链接或子模块'}}
    foreach($path in $old){Assert-Path $destination $path}
}
foreach($path in $files.Keys){Assert-Path $destination $path}
$author=Git $repo @('config','user.name');$email=Git $repo @('config','user.email')
if(!$author -or !$email){throw '源仓库缺少提交作者配置'}
if(!$existing){New-Item -ItemType Directory -Force -Path $destination | Out-Null;Git $destination @('init') | Write-Host}
Git $destination @('config','--local','user.name',$author) | Out-Null
Git $destination @('config','--local','user.email',$email) | Out-Null
foreach($path in $old){if(!$files.ContainsKey($path)){Remove-Item -LiteralPath (Join-Path $destination $path) -Force}}
foreach($path in ($files.Keys | Sort-Object)){
    $to=Join-Path $destination $path
    New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null
    # 只写入已经扫描过的字节，避免并行编辑在检查后改变导出内容。
    [IO.File]::WriteAllBytes($to,$payload[$path])
}
$all=@(@($files.Keys)+$old | Sort-Object -Unique)
# 分批传具体路径，避免命令行长度限制；删除项同样进入索引。
for($i=0;$i -lt $all.Count;$i+=50){Git $destination (@('add','--')+@($all[$i..([Math]::Min($i+49,$all.Count-1))])) | Out-Null}
if(!$Message){$Message='Initial public release'}
if(Git $destination @('diff','--cached','--name-only')){Git $destination (@('commit','--quiet','-m',$Message,'--')+$all) | Write-Host}else{Write-Host '公开副本内容未变化，无需提交'}
Git $destination @('config','--local','pane.publicExport','1') | Out-Null
if(!$SkipTests){
    & (Join-Path $PSScriptRoot 'verify-public.ps1') -Root $destination
}
Write-Host "导出完成：$($files.Count) 个文件"
