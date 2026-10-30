$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
Add-Type -Path "$PSScriptRoot/MemoryMap.cs"
$values=@{PANE_OBSERVE='1';PANE_VERIFY='1';PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'}
$saved=@{};$rows=@()
try {
 foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
 for($i=1;$i-le 3;$i++){
  $prefix=New-Result '013-idle';$process=$null
  try{
   $process=Start-Pane 'aot' $prefix;Wait-Ready $process $prefix
   Send-Control $process $prefix 'launcher-index' 'launcher-index-result' '' 90
   $index=[IO.File]::ReadAllText("$prefix.launcher-index-result");if($index-notmatch '^total=[1-9]'){throw "索引未完成：$index"}
   Start-Sleep -Seconds 10;$process.Refresh()
   $working=$process.WorkingSet64/1MB;$private=$process.PrivateMemorySize64/1MB
   Send-Control $process $prefix 'memory-managed' 'memory-managed-result'
   $rows+=[pscustomobject]@{Run=$i;WorkingMiB=$working;PrivateMiB=$private;Index=$index;Heaps=(Get-Content -Raw -LiteralPath "$prefix.memory-heaps-result"|ConvertFrom-Json);Map=[PaneMemoryMap]::Read($process.Id);Prefix=$prefix}
   Write-Host "idle=$i;working=$working;private=$private;$index"
  }finally{Stop-Pane $process $prefix}
  Get-Content -LiteralPath "$prefix.gesture-stopped"
 }
 $rows|ConvertTo-Json -Depth 6|Set-Content -Encoding UTF8 "$root/bin/results/013-idle.json"
 Write-Host "median-working=$(($rows.WorkingMiB|Sort-Object)[1]);median-private=$(($rows.PrivateMiB|Sort-Object)[1])"
}finally{foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}
