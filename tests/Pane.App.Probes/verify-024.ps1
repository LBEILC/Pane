param([ValidateSet('jit','aot')][string]$Mode='aot')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../src/Pane.App/scripts/common.ps1')
$prefix=New-Result '024'
$binary=Join-Path $root "bin/$Mode/Pane.App.exe"
$environment=@{PANE_RESULT=$prefix;PANE_DATA_DIR=($prefix+'-data');PANE_INSTANCE=$prefix;PANE_OBSERVE='1';PANE_VERIFY='1';PANE_DEMO='0';PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+8;clipboard=Ctrl+Alt+Shift+9'}
$previous=@{}
$original=$null;$replacement=$null
try {
    foreach($key in $environment.Keys){$previous[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$environment[$key])}
    $original=Start-Process -FilePath $binary -ArgumentList @('--autostart') -WindowStyle Hidden -PassThru
    Wait-Ready $original $prefix
    Start-Sleep -Seconds 3
    $original.Refresh();$before=$original.PrivateMemorySize64;$workingBefore=$original.WorkingSet64
    Send-Control $original $prefix 'fonts-test' 'fonts-result'
    Start-Sleep -Seconds 3
    $original.Refresh();$after=$original.PrivateMemorySize64;$workingAfter=$original.WorkingSet64
    $fonts=[IO.File]::ReadAllText("$prefix.fonts-result")
    Remove-Item -LiteralPath "$prefix.ready"
    [IO.File]::WriteAllText("$prefix.restart",'')
    if(!$original.WaitForExit(15000)){throw '重启时旧进程没有退出'}
    if($original.ExitCode -ne 0){throw "旧进程退出失败：$($original.ExitCode)"}
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    while(!(Test-Path -LiteralPath "$prefix.ready")){
        if(Test-Path -LiteralPath "$prefix.error"){throw [IO.File]::ReadAllText("$prefix.error")}
        if([DateTime]::UtcNow -gt $deadline){throw '重启后的新进程没有就绪'}
        Start-Sleep -Milliseconds 50
    }
    $ready=[IO.File]::ReadAllText("$prefix.ready")
    if($ready -notmatch '^pid=(\d+);'){throw '缺少新进程标识'}
    $replacement=Get-Process -Id ([int]$Matches[1])
    if($replacement.Id -eq $original.Id){throw '未替换进程'}
    $command=(Get-CimInstance Win32_Process -Filter "ProcessId=$($replacement.Id)").CommandLine
    if($command -notmatch '--autostart'){throw '重启参数没有保留'}
    $oldCleanup=[IO.File]::ReadAllText("$prefix.stopped")
    if($oldCleanup -ne 'hotkeys=0;tray=0;panels=0'){throw '旧进程清理失败'}
    $gestureCleanup=[IO.File]::ReadAllText("$prefix.gesture-stopped")
    if($gestureCleanup -notmatch 'unhook=1;pause=0'){throw '旧进程钩子清理失败'}
    Stop-Pane $replacement $prefix
    $report="PASS restart old=$($original.Id);new=$($replacement.Id);args=--autostart;oldExit=0;newExit=0;cleanup=$oldCleanup;$gestureCleanup`n$fonts`nprivateBeforeMiB=$([math]::Round($before/1MB,3));privateAfterMiB=$([math]::Round($after/1MB,3));deltaMiB=$([math]::Round(($after-$before)/1MB,3));workingBeforeMiB=$([math]::Round($workingBefore/1MB,3));workingAfterMiB=$([math]::Round($workingAfter/1MB,3))"
    [IO.File]::WriteAllText("$prefix.summary",$report)
    Write-Output $report
    Write-Output "Evidence: $prefix.summary"
} finally {
    [IO.File]::WriteAllText("$prefix.exit",'exit')
    foreach($process in @($replacement,$original)){if($process -and !$process.HasExited){if(!$process.WaitForExit(8000)){$process.Kill();$process.WaitForExit()}}}
    foreach($key in $previous.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key])}
}
