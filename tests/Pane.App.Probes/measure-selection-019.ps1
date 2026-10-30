$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
$names=@('PANE_OBSERVE','PANE_VERIFY','PANE_DEV_HOTKEYS')
$old=@{};foreach($name in $names){$old[$name]=[Environment]::GetEnvironmentVariable($name)}
$prefix=New-Result '019-idle';$process=$null
try {
 $env:PANE_OBSERVE='1';$env:PANE_VERIFY='1';$env:PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'
 $process=Start-Pane 'aot' $prefix $false ($prefix+'-data')
 Wait-Ready $process $prefix
 Start-Sleep -Seconds 10
 $process.Refresh()
 $row=[pscustomobject]@{WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB;ProcessId=$process.Id;DataDirectory=$prefix+'-data'}
 $row|ConvertTo-Json|Set-Content -Encoding UTF8 ($prefix+'.json')
 $row|Format-List
}finally{
 try{Stop-Pane $process $prefix}finally{foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$old[$name])}}
}
Get-Content -LiteralPath ($prefix+'.gesture-stopped')
Get-Content -LiteralPath ($prefix+'.clipboard-stopped')
