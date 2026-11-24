param([string]$Stage,[string]$Dist,[string]$Version,[switch]$SkipHistory)
$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot 'delta/Pane.Delta.csproj'
& dotnet @('build',$project,'-c','Release','--nologo') | Out-Host
if($LASTEXITCODE){throw '补丁工具构建失败'}
$tool=Join-Path $PSScriptRoot 'delta/bin/Release/net10.0-windows/Pane.Delta.dll'
function Invoke-Delta([string[]]$Arguments){
    & dotnet $tool @Arguments | Out-Host
    if($LASTEXITCODE){throw "补丁工具失败：$($Arguments[0])"}
}
$manifest=Join-Path $Dist "Pane-Manifest-$Version.json"
Invoke-Delta @('manifest',$Stage,$Version,$manifest)
$manifest
$cache=Join-Path $Dist 'payloads'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$target=Join-Path $cache $Version
if(!(Test-Path -LiteralPath $target)){
    $temporary=Join-Path $cache ([guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $Stage -Destination $temporary -Recurse
    Move-Item -LiteralPath $temporary -Destination $target
}
if($SkipHistory){return}
try {
    $releases=& gh @('release','list','--repo','LBEILC/Pane','--exclude-drafts','--exclude-pre-releases','--limit','100','--json','tagName,publishedAt')
    if($LASTEXITCODE){throw '无法列出公开版本'}
    $history=@(($releases -join '') | ConvertFrom-Json | ForEach-Object { $_ } | Where-Object {
        $_.tagName -match '^v\d+\.\d+\.\d+$' -and [version]$_.tagName.Substring(1) -lt [version]$Version
    } | Sort-Object publishedAt -Descending | Select-Object -First 5)
} catch {Write-Warning "跳过历史补丁：$($_.Exception.Message)";return}
# 同时生成两个补丁，限制 MSDelta 处理大型可执行文件时的峰值内存。
$pending=[Collections.Generic.List[object]]::new()
function Receive-Patch {
    $job=Wait-Job -Job $pending.ToArray() -Any
    try {
        $result=Receive-Job -Job $job -ErrorAction Stop
        if(!$result.Ok){throw $result.Log}
        Write-Host $result.Log
        $result.Path
    } catch {Write-Warning "跳过 $($job.Name) 的补丁：$($_.Exception.Message)"}
    finally {$null=$pending.Remove($job);Remove-Job -Job $job}
}
try {
foreach($release in $history){
    $old=$release.tagName.Substring(1)
    $baseline=Join-Path $cache $old
    try {
        if(!(Test-Path -LiteralPath $baseline)){
            $downloads=Join-Path (Split-Path $PSScriptRoot -Parent) "docs/research/refs/releases/$old"
            New-Item -ItemType Directory -Path $downloads -Force | Out-Null
            $name="Pane-Setup-$old.exe"
            & gh @('release','download',$release.tagName,'--repo','LBEILC/Pane','--dir',$downloads,'--pattern',$name,'--pattern','SHA256SUMS.txt','--clobber') | Out-Host
            if($LASTEXITCODE){throw '旧包下载失败'}
            $sums=Get-Content -LiteralPath (Join-Path $downloads 'SHA256SUMS.txt') -Encoding UTF8
            $hash=(Get-FileHash -LiteralPath (Join-Path $downloads $name)).Hash
            if(@($sums | Where-Object {$_ -match ('^'+$hash+'\s+\*?'+[regex]::Escape($name)+'$')}).Count -ne 1){throw '旧包校验失败'}
            $temporary=Join-Path $cache ([guid]::NewGuid().ToString('N'))
            Invoke-Delta @('extract',(Join-Path $downloads $name),$temporary)
            Move-Item -LiteralPath $temporary -Destination $baseline
        }
        $patch=Join-Path $Dist "Pane-Patch-$old-$Version.zip"
        if($pending.Count -ge 2){Receive-Patch}
        $job=Start-Job -Name "$old-$Version" -ArgumentList @($tool,$baseline,$Stage,$old,$Version,$manifest,$patch) -ScriptBlock {
            param($Tool,$Baseline,$Stage,$Old,$Version,$Manifest,$Patch)
            $log=& dotnet @($Tool,'patch',$Baseline,$Stage,$Old,$Version,$Manifest,$Patch) 2>&1
            [pscustomobject]@{Ok=($LASTEXITCODE -eq 0);Path=$Patch;Log=($log -join [Environment]::NewLine)}
        }
        $pending.Add($job)
    } catch {Write-Warning "跳过 $old 至 $Version 的补丁：$($_.Exception.Message)"}
}
while($pending.Count){Receive-Patch}
} finally {
    foreach($job in $pending){Stop-Job -Job $job;Remove-Job -Job $job}
}
