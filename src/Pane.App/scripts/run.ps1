param([ValidateSet('jit','aot')][string]$Mode='aot',[int]$Seconds=0,[switch]$AllowWithProduction)
. "$PSScriptRoot/common.ps1"
if(!$AllowWithProduction){
    $sha=[Security.Cryptography.SHA256]::Create()
    try {$hash=$sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($env:USERNAME+'|production|'));$key='Local\Pane-'+([BitConverter]::ToString($hash).Replace('-','').Substring(0,24))}finally{$sha.Dispose()}
    $existing=$null
    if([Threading.Mutex]::TryOpenExisting($key,[ref]$existing)){$existing.Dispose();throw '正式版正在运行，请先退出正式版；确需并行时使用 -AllowWithProduction。'}
}
$prefix=New-Result 'run';$process=$null
$data=if($env:PANE_DATA_DIR){$env:PANE_DATA_DIR}else{Join-Path $root 'bin/data'}
try {$process=Start-Pane $Mode $prefix $false $data $false;try {Wait-Ready $process $prefix} catch {if($process.HasExited -and $process.ExitCode -eq 0 -and !(Test-Path -LiteralPath "$prefix.ready")){Write-Host '命令已转交现有实例。';$process=$null;return};throw};Get-Content -LiteralPath "$prefix.ready";Write-Host "退出控制文件：$prefix.exit";if($Seconds -gt 0){$process.WaitForExit($Seconds*1000)|Out-Null}else{$process.WaitForExit()}}
finally {Stop-Pane $process $prefix}
