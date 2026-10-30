param([int]$RunCount=3)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
$prefixBase=New-Result '014-measure'
$native=Join-Path $root 'bin/native/Pane.Native.dll'
$fixtures=@{empty="$prefixBase-empty";history500="$prefixBase-history500"}
& dotnet @('run','--project',"$repo/tests/Pane.Clipboard.Tests/Pane.Clipboard.Tests.csproj",'-c','Release','-p:NuGetAudit=false','--',$native,'seed',$fixtures.history500)
if($LASTEXITCODE-ne 0){throw '生成假历史失败'}
$values=@{PANE_OBSERVE='1';PANE_VERIFY='1';PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'}
$saved=@{};$rows=@()
try {
 foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
 foreach($fixture in @('empty','history500')){
  for($i=1;$i-le $RunCount;$i++){
   $prefix=New-Result '014-idle';$process=$null
   try {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $process=Start-Pane 'aot' $prefix $false $fixtures[$fixture];Wait-Ready $process $prefix
    $ready=$watch.Elapsed.TotalMilliseconds
    Send-Control $process $prefix 'clipboard-status' 'clipboard-status-result'
    $status=[IO.File]::ReadAllText("$prefix.clipboard-status-result")
    $expected=if($fixture-eq 'empty'){'count=0;images=0;'}else{'count=500;images=50;'}
    if(!$status.StartsWith($expected)-or $status-notmatch 'registered=True;failures=0;storageFailures=0'){throw '剪贴板状态不符'}
    Send-Control $process $prefix 'launcher-index' 'launcher-index-result' '' 300
    $allReady=$watch.Elapsed.TotalMilliseconds
    $child=[IO.File]::ReadAllText("$prefix.launcher-process-result")
    if($child-notmatch 'childOutcome=completed;'){throw '索引辅助进程未完成'}
    $childId=[int]([regex]::Match($child,'childPid=(\d+)').Groups[1].Value)
    if(Get-Process -Id $childId -ErrorAction SilentlyContinue){throw '索引辅助进程未退出'}
    Start-Sleep -Seconds 10;$process.Refresh()
    $rows+=[pscustomobject]@{Fixture=$fixture;Run=$i;ReadyMs=$ready;AllReadyMs=$allReady;WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB;Clipboard=$status;Prefix=$prefix}
    Write-Host ($rows[-1]|ConvertTo-Json -Compress)
   }finally{Stop-Pane $process $prefix}
   $stopped=[IO.File]::ReadAllText("$prefix.clipboard-stopped")
   if($stopped-notmatch '^registered=True;removed=True;' -or $stopped-notmatch 'failures=0;storageFailures=0$'){throw '剪贴板监听清理失败'}
  }
 }
 $rows|ConvertTo-Json -Depth 4|Set-Content -Encoding UTF8 "$prefixBase.json"
 foreach($fixture in @('empty','history500')){
  $group=@($rows|Where-Object Fixture -eq $fixture);$median=@($group.WorkingMiB|Sort-Object)[[int][Math]::Floor($group.Count/2)]
  Write-Host "fixture=$fixture;medianWorkingMiB=$median;deltaFrom016=$($median-119.49)"
  if($median-gt 150){throw "空闲工作集超过目标：$median MiB；证据 $prefixBase.json"}
 }
 Write-Host "allProcessesStopped=True;results=$prefixBase.json"
}finally{foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}
