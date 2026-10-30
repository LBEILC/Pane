param([ValidateSet('jit','aot')][string]$Mode='aot')
. "$PSScriptRoot/common.ps1"
$oldVerify=$env:PANE_VERIFY
try {
$env:PANE_VERIFY='1'
$rows=@()
for($i=1;$i-le 3;$i++){
    $prefix=New-Result "measure-$Mode-$i";$process=$null
    try {
        $process=Start-Pane $Mode $prefix;Wait-Ready $process $prefix;Start-Sleep -Seconds 10;$process.Refresh()
        $rows+=[pscustomobject]@{Mode=$Mode;Run=$i;State='idle';WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB}
        Send-Control $process $prefix 'show-settings' 'shown-settings';Start-Sleep -Seconds 10;$process.Refresh()
        $rows+=[pscustomobject]@{Mode=$Mode;Run=$i;State='settings-open';WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB}
        Send-Control $process $prefix 'hide' 'hidden';Start-Sleep -Seconds 10;$process.Refresh()
        $rows+=[pscustomobject]@{Mode=$Mode;Run=$i;State='settings-closed';WorkingMiB=$process.WorkingSet64/1MB;PrivateMiB=$process.PrivateMemorySize64/1MB}
    } finally {Stop-Pane $process $prefix}
}
$rows|Export-Csv -NoTypeInformation -Encoding UTF8 "$root/bin/results/measure.csv"
$rows|Format-Table
foreach($state in @('idle','settings-open','settings-closed')){$sample=@($rows|Where-Object State -eq $state);Write-Host "$state 工作集中位数=$(($sample.WorkingMiB|Sort-Object)[1]) MiB；私有字节中位数=$(($sample.PrivateMiB|Sort-Object)[1]) MiB"}

} finally {$env:PANE_VERIFY=$oldVerify}
