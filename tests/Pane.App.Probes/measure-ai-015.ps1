param([int]$RunCount=3)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/src/Pane.App/scripts/common.ps1"
$prefixBase=New-Result '015-measure'
$server=$null
$values=@{PANE_OBSERVE='1';PANE_VERIFY='1';PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'}
$saved=@{};$rows=@()
try {
 foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
 $serverExe=Join-Path $repo 'tests/Pane.Ai.Tests/bin/Release/net10.0-windows/Pane.Ai.Tests.exe'
 $server=Start-Process -FilePath $serverExe -ArgumentList @('server',$prefixBase) -WindowStyle Hidden -PassThru
 $deadline=[DateTime]::UtcNow.AddSeconds(15)
 while(!(Test-Path -LiteralPath "$prefixBase.url")){if($server.HasExited-or [DateTime]::UtcNow-gt $deadline){throw '假服务未就绪'};Start-Sleep -Milliseconds 50}
 $url=[IO.File]::ReadAllText("$prefixBase.url")
 for($i=1;$i-le $RunCount;$i++){
  $prefix=New-Result '015-idle';$process=$null
  try {
   $process=Start-Pane 'aot' $prefix $false "$prefix-data";Wait-Ready $process $prefix
   Send-Control $process $prefix 'launcher-index' 'launcher-index-result' '' 300
   Start-Sleep -Seconds 10;$process.Refresh();$before=$process.WorkingSet64/1MB
   Send-Control $process $prefix 'ai-test' 'ai-test-result' $url
   if([IO.File]::ReadAllText("$prefix.ai-test-result")-ne 'success'){throw '假服务请求失败'}
   Start-Sleep -Seconds 10;$process.Refresh();$after=$process.WorkingSet64/1MB
   $rows+=[pscustomobject]@{Run=$i;BeforeMiB=$before;AfterMiB=$after;DeltaMiB=$after-$before;Prefix=$prefix}
   Write-Host ($rows[-1]|ConvertTo-Json -Compress)
  }finally{Stop-Pane $process $prefix}
 }
 $rows|ConvertTo-Json -Depth 4|Set-Content -Encoding UTF8 "$prefixBase.json"
 $beforeMedian=@($rows.BeforeMiB|Sort-Object)[[int][Math]::Floor($rows.Count/2)]
 $afterMedian=@($rows.AfterMiB|Sort-Object)[[int][Math]::Floor($rows.Count/2)]
 Write-Host "beforeMedianMiB=$beforeMedian;afterMedianMiB=$afterMedian;deltaMiB=$($afterMedian-$beforeMedian)"
 if($beforeMedian-gt 150-or $afterMedian-gt 150){throw '空闲工作集超过 150 MiB'}
}finally{
 if($server){[IO.File]::WriteAllText("$prefixBase.stop",'stop');if(!$server.WaitForExit(8000)){$server.Kill();$server.WaitForExit();throw '假服务未正常退出'}}
 foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}
}
Write-Host "allProcessesStopped=True;results=$prefixBase.json"
