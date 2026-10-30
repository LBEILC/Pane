$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
Add-Type -Path "$PSScriptRoot/MemoryMap.cs"
$saved=@{}
$values=@{PANE_OBSERVE='1';PANE_VERIFY='1';PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C';PANE_MEMORY_STARTUP_LEVEL='0'}
$prefix=New-Result '012-components';$process=$null;$rows=@()
try {
 foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
 $components=@('runtime','launcher','clipboard','super','hint','shell','services')
 for($level=0;$level-le 6;$level++){
  $env:PANE_MEMORY_STARTUP_LEVEL=$level.ToString();$prefix=New-Result "012-components-$level";$process=$null
  try{
  $process=Start-Pane 'aot' $prefix;Wait-Ready $process $prefix
  Start-Sleep -Seconds 10;$process.Refresh()
  $working=$process.WorkingSet64/1MB;$private=$process.PrivateMemorySize64/1MB
  Send-Control $process $prefix 'memory-managed' 'memory-managed-result'
  $rows+=[pscustomobject]@{Added=$components[$level];Level=$level;WorkingMiB=$working;PrivateMiB=$private;ManagedBytes=[long][IO.File]::ReadAllText("$prefix.memory-managed-result");Map=[PaneMemoryMap]::Read($process.Id);Prefix=$prefix}
  Write-Host "$($components[$level]) working=$working private=$private"
  }finally{Stop-Pane $process $prefix;$process=$null}
 }
 $result=New-Result '012-components';$rows|ConvertTo-Json -Depth 4|Set-Content -Encoding UTF8 "$result.components.json"
 Write-Host "PREFIX=$result"
}finally{try{Stop-Pane $process $prefix}finally{foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}}
