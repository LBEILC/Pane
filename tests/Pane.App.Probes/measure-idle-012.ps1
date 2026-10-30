$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
$oldObserve=$env:PANE_OBSERVE;$oldVerify=$env:PANE_VERIFY;$oldHotkeys=$env:PANE_DEV_HOTKEYS
$env:PANE_OBSERVE='1';$env:PANE_VERIFY='1';$env:PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'
$rows=@()
try {
 for($i=1;$i-le 3;$i++){
  $prefix=New-Result '012-idle';$process=$null
  try{$process=Start-Pane 'aot' $prefix;Wait-Ready $process $prefix;Start-Sleep -Seconds 10;$process.Refresh();$rows+=[pscustomobject]@{Run=$i;WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB};Write-Host "idle=$i;working=$($process.WorkingSet64/1MB);private=$($process.PrivateMemorySize64/1MB)"}
  finally{Stop-Pane $process $prefix}
  Get-Content -LiteralPath "$prefix.gesture-stopped"
 }
 $rows|Export-Csv -NoTypeInformation -Encoding UTF8 "$root/bin/results/012-idle.csv"
 Write-Host "median-working=$(($rows.WorkingMiB|Sort-Object)[1]);median-private=$(($rows.PrivateMiB|Sort-Object)[1])"
}finally{$env:PANE_OBSERVE=$oldObserve;$env:PANE_VERIFY=$oldVerify;$env:PANE_DEV_HOTKEYS=$oldHotkeys}
