$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path (Split-Path (Split-Path $root -Parent) -Parent) 'scripts/remove-tree.ps1')
function New-Result([string]$name){
    $dir=Join-Path $root 'bin/results';New-Item -ItemType Directory -Force $dir | Out-Null
    Remove-StaleResults $dir
    Join-Path $dir ($name+'-'+[Guid]::NewGuid().ToString('N'))
}
# 7 天没动过的旧结果顺手删掉，免得无限累积；同名带 .error 失败标记的整组留作现场。
function Remove-StaleResults([string]$dir){
    $stale=[DateTime]::Now.AddDays(-7)
    $entries=@(Get-ChildItem -LiteralPath $dir -Force)
    $failed=@{};foreach($entry in $entries){if($entry.Name -like '*.error'){$failed[$entry.Name.Substring(0,$entry.Name.Length-6)]=$true}}
    foreach($entry in $entries){
        if($entry.LastWriteTime -gt $stale){continue}
        $base=if($entry.PSIsContainer){$entry.Name -replace '-data$',''}else{$entry.Name.Split('.')[0]}
        if($failed.ContainsKey($base)){continue}
        try {Remove-LinkSafeTree $entry.FullName}catch{Write-Warning "旧结果没删掉：$($entry.FullName)"}
    }
}
function Start-Pane([string]$mode,[string]$prefix,[bool]$demo=$false,[string]$dataDirectory='',[bool]$isolated=$true){
    if(!$dataDirectory){$dataDirectory=$prefix+'-data'}
    $settings=@{PANE_RESULT=$prefix;PANE_DEMO=([int]$demo).ToString();PANE_DATA_DIR=$dataDirectory;PANE_INSTANCE=$(if($isolated){$prefix}else{''})}
    $old=@{}
    try {
        foreach($key in $settings.Keys){$old[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$settings[$key])}
        $binaryRoot=if($env:PANE_BIN_DIR){$env:PANE_BIN_DIR}else{Join-Path $root 'bin'}
        Start-Process -FilePath "$binaryRoot/$mode/Pane.App.exe" -WindowStyle Hidden -PassThru
    } finally {foreach($key in $old.Keys){[Environment]::SetEnvironmentVariable($key,$old[$key])}}
}
function Wait-Ready($process,[string]$prefix,[string]$suffix='ready',[int]$timeout=40){
    $end=[DateTime]::UtcNow.AddSeconds($timeout)
    while(!(Test-Path -LiteralPath "$prefix.$suffix")){
        if(Test-Path -LiteralPath "$prefix.error"){throw [IO.File]::ReadAllText("$prefix.error")}
        if($process.HasExited){throw "进程提前退出，退出码 $($process.ExitCode)"}
        if([DateTime]::UtcNow -gt $end){throw "等待 $suffix 超时"}
        Start-Sleep -Milliseconds 50
    }
}
function Send-Control($process,[string]$prefix,[string]$command,[string]$ack,[string]$text='',[int]$timeout=40){
    Remove-Item -LiteralPath "$prefix.$ack" -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText("$prefix.command-tmp",$text)
    Move-Item -LiteralPath "$prefix.command-tmp" -Destination "$prefix.$command"
    Wait-Ready $process $prefix $ack $timeout
    Write-Host "$command -> $ack"
}
function Stop-Pane($process,[string]$prefix){
    if(!$process){return}
    if(!$process.HasExited){[IO.File]::WriteAllText("$prefix.exit",'exit');if(!$process.WaitForExit(8000)){$process.Kill();$process.WaitForExit();throw '正常清理超时，已终止本次测试进程'}}
    if($process.ExitCode -ne 0){throw "进程退出码 $($process.ExitCode)"}
    if(!(Test-Path -LiteralPath "$prefix.stopped")){throw '缺少清理记录'}
    if([IO.File]::ReadAllText("$prefix.stopped")-ne 'hotkeys=0;tray=0;panels=0'){throw '清理不完整'}
}
