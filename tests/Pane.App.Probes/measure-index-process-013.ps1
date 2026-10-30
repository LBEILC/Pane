$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
$values=@{PANE_OBSERVE='1';PANE_VERIFY='1';PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'}
$saved=@{};$rows=@();$reuse='';$cached=''
try {
 foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
 for($i=1;$i-le 3;$i++){
  $prefix=New-Result '013-process';$process=$null
  try {
   $process=Start-Pane 'aot' $prefix;Wait-Ready $process $prefix
   Send-Control $process $prefix 'launcher-index' 'launcher-index-result' '' 90
   $index=[IO.File]::ReadAllText("$prefix.launcher-index-result")
   $child=[IO.File]::ReadAllText("$prefix.launcher-process-result")
   if($index-notmatch '^total=[1-9]' -or $child-notmatch 'pinyinLoads=0;' -or $child-notmatch 'childOutcome=completed;'){throw "索引验证失败：$index;$child"}
   $childId=[int]([regex]::Match($child,'childPid=(\d+)').Groups[1].Value)
   if(Get-Process -Id $childId -ErrorAction SilentlyContinue){throw '索引进程未退出'}
   Start-Sleep -Seconds 10;$process.Refresh()
   $rows+=[pscustomobject]@{Run=$i;WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB;Index=$index;Child=$child;Prefix=$prefix}
   Write-Host ($rows[-1]|ConvertTo-Json -Compress)
   if($i-eq 1){$reuse=$prefix+'-data'}
  }finally{Stop-Pane $process $prefix}
  Get-Content -LiteralPath "$prefix.gesture-stopped"
 }
 $prefix=New-Result '013-cached';$process=$null
 try{
  $process=Start-Pane 'aot' $prefix $false $reuse;Wait-Ready $process $prefix
  Send-Control $process $prefix 'launcher-cached' 'launcher-cached-result'
  $cached=[IO.File]::ReadAllText("$prefix.launcher-cached-result");Write-Host $cached
  if($cached-notmatch '^apps=[1-9]' -or $cached-notmatch 'indexPending=True' -or $cached-notmatch 'pinyinLoads=0'){throw '持久化启动验证失败'}
  Send-Control $process $prefix 'launcher-index' 'launcher-index-result' '' 90
 }finally{Stop-Pane $process $prefix}
 $median=($rows.WorkingMiB|Sort-Object)[1]
 [pscustomobject]@{Runs=$rows;MedianWorkingMiB=$median;Cached=$cached}|ConvertTo-Json -Depth 5|Set-Content -Encoding UTF8 "$root/bin/results/013-process.json"
 Write-Host "median-working=$median;all-test-processes-stopped=True"
 if($median-gt 120){throw "空闲工作集超出目标：$median MiB"}
}finally{foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}
