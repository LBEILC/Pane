param([ValidateSet('jit','aot')][string]$Mode='aot',[switch]$Memory,[switch]$Import,[ValidateRange(1,100)][int]$Rounds=10,[switch]$Classify,[switch]$EmptyIsland,[ValidateRange(0,8)][int]$Sections=8)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
$oldObserve=$env:PANE_OBSERVE;$oldVerify=$env:PANE_VERIFY;$oldHotkeys=$env:PANE_DEV_HOTKEYS
$env:PANE_OBSERVE='1';$env:PANE_VERIFY='1';$env:PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'
Add-Type -Path "$PSScriptRoot/MemoryMap.cs"
$prefix=New-Result '012-verify';$process=$null;$rows=@();$maps=@()
try {
 $process=Start-Pane $Mode $prefix;Wait-Ready $process $prefix
 Write-Host "PREFIX=$prefix"
 if($Memory){
  Start-Sleep -Seconds 10;$process.Refresh();$rows+=[pscustomobject]@{Run=0;WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB}
  if($Classify){Send-Control $process $prefix 'memory-managed' 'memory-managed-result';$maps+=[pscustomobject]@{Run=0;ManagedBytes=[long][IO.File]::ReadAllText("$prefix.memory-managed-result");Heaps=$(if(Test-Path "$prefix.memory-heaps-result"){Get-Content -Raw "$prefix.memory-heaps-result"|ConvertFrom-Json});Map=[PaneMemoryMap]::Read($process.Id)}}
  for($run=1;$run-le $Rounds;$run++){
   if($EmptyIsland){Send-Control $process $prefix 'empty-island-open' 'empty-island-opened';Start-Sleep -Milliseconds 500}else{Send-Control $process $prefix 'show-settings' 'shown-settings'}
   if($run-eq 1){$process.Refresh();[pscustomobject]@{WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB}|ConvertTo-Json|Set-Content -Encoding UTF8 "$prefix.first-open.json"}
   for($section=0;!$EmptyIsland-and $section-lt $Sections;$section++){Send-Control $process $prefix 'settings-section' 'section-shown' $section.ToString()}
   if($EmptyIsland){Send-Control $process $prefix 'empty-island-close' 'empty-island-closed'}else{Send-Control $process $prefix 'hide' 'hidden'};Start-Sleep -Seconds 10;$process.Refresh()
   $rows+=[pscustomobject]@{Run=$run;WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB}
   if($Classify){Send-Control $process $prefix 'memory-managed' 'memory-managed-result';$maps+=[pscustomobject]@{Run=$run;ManagedBytes=[long][IO.File]::ReadAllText("$prefix.memory-managed-result");Heaps=$(if(Test-Path "$prefix.memory-heaps-result"){Get-Content -Raw "$prefix.memory-heaps-result"|ConvertFrom-Json});Map=[PaneMemoryMap]::Read($process.Id)}}
   Write-Host "closed=$run;working=$($process.WorkingSet64/1MB);private=$($process.PrivateMemorySize64/1MB)"
  }
  $maps|ConvertTo-Json -Depth 4|Set-Content -Encoding UTF8 "$prefix.maps.json"
  $rows|Export-Csv -NoTypeInformation -Encoding UTF8 "$prefix.memory.csv"
  Send-Control $process $prefix 'settings-gc' 'settings-gc-result';Get-Content -LiteralPath "$prefix.settings-gc-result"
  if($Classify){Start-Sleep -Seconds 1;Send-Control $process $prefix 'memory-managed' 'memory-managed-result';$process.Refresh();[pscustomobject]@{WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB;Heaps=(Get-Content -Raw "$prefix.memory-heaps-result"|ConvertFrom-Json);Map=[PaneMemoryMap]::Read($process.Id)}|ConvertTo-Json -Depth 4|Set-Content -Encoding UTF8 "$prefix.post-gc.json"}
 } else {
  foreach($value in @('true','true','false','false')){
   Send-Control $process $prefix 'hotkeys-suspend' 'hotkeys-requested' $value;Start-Sleep -Milliseconds 100
   Send-Control $process $prefix 'service-status' 'service-status-result';$status=[IO.File]::ReadAllText("$prefix.service-status-result");Write-Host $status
   $expected=if($value-eq 'true'){'hotkeys=0;pause=0;'}else{'hotkeys=2;pause=1;'}
   if(!$status.StartsWith($expected)){throw "注册数不符：$status"}
  }
  Send-Control $process $prefix 'super-test' 'super-tested'
  if([IO.File]::ReadAllText("$prefix.selection-order") -ne "SetSelection
Show
"){throw '选区与显示顺序错误'}
  if($Import){& "$repo/scripts/import-gestures.ps1" -ControlPrefix $prefix;$result=[IO.File]::ReadAllText("$prefix.import-result");if(!$result.StartsWith('global=52;rules=7;disabled=4;unsupported=0')){throw '导入计数不符'}}
 }
} finally {Stop-Pane $process $prefix;$env:PANE_OBSERVE=$oldObserve;$env:PANE_VERIFY=$oldVerify;$env:PANE_DEV_HOTKEYS=$oldHotkeys}
Get-Content -LiteralPath "$prefix.gesture-stopped"
