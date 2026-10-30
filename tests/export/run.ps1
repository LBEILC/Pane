$ErrorActionPreference='Stop'
$source=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$parent=Join-Path $source 'out/export-tests'
$sandbox=Join-Path $parent ([Guid]::NewGuid().ToString('N'))
$repo=Join-Path $sandbox 'source';$output=Join-Path $sandbox 'public'
$utf8=[Text.UTF8Encoding]::new($true);$script:passed=0
function Put([string]$Path,[string]$Text){$full=Join-Path $repo $Path;New-Item -ItemType Directory -Force (Split-Path $full -Parent)|Out-Null;[IO.File]::WriteAllText($full,$Text,$utf8)}
function G([string[]]$Arguments,[string]$Root=$repo){$result=& git @('-C',$Root,'-c','core.autocrlf=false') @Arguments;if($LASTEXITCODE -ne 0){throw ($result -join "`n")};return ($result -join "`n")}
function Save([string[]]$Paths){G (@('add','--')+$Paths)|Out-Null;G (@('commit','-m','Fixture','--')+$Paths)|Out-Null}
function Check([bool]$Condition,[string]$Name){if(!$Condition){throw "FAIL: $Name"};$script:passed++;Write-Host "PASS: $Name"}
function Export([bool]$Success,[string[]]$Extra=@(),[string]$Reason=''){
    $previous=$ErrorActionPreference;$ErrorActionPreference='Continue'
    $log=& powershell.exe @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$repo/scripts/export-public.ps1",'-Output',$output,'-SkipTests') @Extra 2>&1
    $code=$LASTEXITCODE;$ErrorActionPreference=$previous
    if(($code -eq 0) -ne $Success){throw "导出退出码 $code : $($log -join "`n")"}
    if($Reason -and ($log -join "`n") -notmatch $Reason){throw "缺少预期诊断 $Reason : $($log -join "`n")"}
}
try {
    . (Join-Path $source 'scripts/forbidden-scan.ps1')
    $policyPath=Join-Path $source 'docs/internal.md'
    # 内部验收读取真实政策；公开源码只保留虚构用户名和相同边界结构。
    $nameRule=if(Test-Path -LiteralPath $policyPath){@(Read-ForbiddenRules $policyPath)[0]}else{[regex]::new('(?<![A-Z])TESTUSER(?![A-Z])','IgnoreCase')}
    $name=[regex]::Match($nameRule.ToString(),'(?<=\))[A-Z]+(?=\()').Value
    Check ($name -and $nameRule.IsMatch((('C:','Users',$name,'x') -join '\')) -and $nameRule.IsMatch($name) -and !$nameRule.IsMatch(('Z'+$name.ToLowerInvariant()+'lelflG'))) '用户名真实路径及独立词命中，字符表不命中'
    New-Item -ItemType Directory -Force $repo|Out-Null
    $scanFile=Join-Path $repo 'scan.bin'
    foreach($encoding in $ForbiddenEncodings){
        [IO.File]::WriteAllBytes($scanFile,$encoding.GetBytes('private-token'))
        $rejected=$false
        try {Assert-ForbiddenFiles -Files @($scanFile) -Rules @([regex]::new('private-token'))}catch {if($_ -notmatch '打包扫描失败'){throw};$rejected=$true}
        Check $rejected "打包扫描拒绝 $($encoding.WebName)"
    }
    Remove-Item -LiteralPath $scanFile
    G @('init')|Out-Null;G @('config','user.name','Fixture')|Out-Null;G @('config','user.email','fixture@example.invalid')|Out-Null
    # 使用虚构规则，不把真实禁用词写入公开测试源码。
    Put 'docs/internal.md' ('```forbidden'+"`n"+'private-token'+"`n"+'[A-Z]:[\\/]+Users[\\/]+(?!你)'+"`n"+'```')
    Put '.gitignore' "ignored/`n*.tmp`n"
    Put 'src/hello.txt' 'hello';Put 'src/forced.tmp' 'ignored even when tracked';Put 'ignored/a.txt' 'not public'
    Put 'public/docs/guide.md' '[source](../src/hello.txt)'
    Put 'README.md' '[guide](docs/guide.md)'
    Put 'src/example.txt' 'C:\Users\你\Pictures'
    New-Item -ItemType Directory -Force "$repo/scripts"|Out-Null
    Copy-Item -LiteralPath "$source/scripts/export-public.ps1" -Destination "$repo/scripts/export-public.ps1"
    Copy-Item -LiteralPath "$source/scripts/forbidden-scan.ps1" -Destination "$repo/scripts/forbidden-scan.ps1"
    $initial=@('docs/internal.md','.gitignore','src/hello.txt','public/docs/guide.md','README.md','src/example.txt','scripts/export-public.ps1')
    G @('add','-f','--','src/forced.tmp')|Out-Null
    Save ($initial+@('src/forced.tmp','scripts/forbidden-scan.ps1'))
    Export $true
    Check ((Test-Path "$output/src/hello.txt") -and (Test-Path "$output/docs/guide.md") -and !(Test-Path "$output/docs/internal.md") -and !(Test-Path "$output/public")) '白名单及文档映射'
    Check (!(Test-Path "$output/ignored") -and !(Test-Path "$output/src/forced.tmp")) '忽略文件不导出'
    Check (Test-Path "$output/src/example.txt") '中文示意用户名例外'
    Check ((G @('rev-list','--count','HEAD') $output) -eq '1' -and (G @('config','--local','pane.publicExport') $output) -eq '1') '首次提交及本地标记'
    foreach($kind in @('text','utf16','path')){
        $path=if($kind -eq 'path'){'src/private-token.bin'}else{'src/bad.bin'}
        Put $path 'private-token'
        if($kind -eq 'utf16'){[IO.File]::WriteAllBytes((Join-Path $repo $path),([byte[]]@(0,0,0,0)+[Text.Encoding]::Unicode.GetBytes('private-token')))}
        Save @($path)
        $before=G @('rev-parse','HEAD') $output
        Export $false @('-ScanOnly') '规则 1'
        Check ((G @('rev-parse','HEAD') $output) -eq $before) "禁用词 $kind 拒绝且不写入"
        G @('rm','--',$path)|Out-Null;G @('commit','-m','Remove fixture','--',$path)|Out-Null
    }
    Put 'README.md' '[broken](docs/missing.md)';Export $false @('-ScanOnly') '导出集合外链接';Check $true '坏链接拒绝'
    Put 'README.md' '[guide](docs/guide.md)';Put 'src/hello.txt' 'changed'
    Export $false @('-Message','Update') '工作区不干净';Check $true '导出工作区不干净拒绝'
    Save @('src/hello.txt');Put 'src/new.txt' 'new';Save @('src/new.txt')
    G @('rm','--','src/example.txt')|Out-Null;G @('commit','-m','Delete fixture','--','src/example.txt')|Out-Null
    Export $true @('-Message','Update')
    Check (([IO.File]::ReadAllText("$output/src/hello.txt") -eq 'changed') -and (Test-Path "$output/src/new.txt") -and !(Test-Path "$output/src/example.txt") -and (G @('rev-list','--count','HEAD') $output) -eq '2') '增量新增修改删除和历史保留'
    Put 'docs/internal-note.txt' 'local only';Export $true @('-Message','No change');Check $true '导出集合以外的修改不阻塞'
    [IO.File]::WriteAllText("$output/foreign.txt",'keep')
    Export $false @('-Message','Update') '未跟踪';Check ([IO.File]::ReadAllText("$output/foreign.txt") -eq 'keep') '副本外来文件拒绝'
    Remove-Item -LiteralPath "$output/foreign.txt"
    G @('config','--local','--unset','pane.publicExport') $output|Out-Null
    Export $false @('-Message','Update');Check $true '非空无标记目录拒绝'
    G @('config','--local','pane.publicExport','1') $output|Out-Null
    # Git 链接模式不需要操作系统创建链接权限。
    $blob=G @('rev-parse','HEAD:src/hello.txt')
    G @('update-index','--add','--cacheinfo',"120000,$blob,src/link")|Out-Null
    G @('commit','-m','Link fixture')|Out-Null
    Export $false @('-ScanOnly') '符号链接';Check $true '符号链接拒绝'
    Write-Host "通过：$script:passed 项"
} finally {
    $resolved=[IO.Path]::GetFullPath($sandbox)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath($parent)+'\',[StringComparison]::OrdinalIgnoreCase)){throw '拒绝清理范围外目录'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
