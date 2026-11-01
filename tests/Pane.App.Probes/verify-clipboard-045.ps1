param([Parameter(Mandatory=$true)][string]$AppDirectory,[Parameter(Mandatory=$true)][string]$DataDirectory,[switch]$Baseline)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$app=[IO.Path]::GetFullPath($AppDirectory)
$data=[IO.Path]::GetFullPath($DataDirectory)
if($data -eq (Join-Path $env:APPDATA 'Pane')){throw '禁止使用正式数据目录'}
$test=Join-Path $repo 'tests/Pane.Clipboard.Tests/bin/Release/net10.0-windows/Pane.Clipboard.Tests.exe'
if(!(Test-Path -LiteralPath $data)){
    & $test @((Join-Path $app 'Pane.Native.dll'),'production-fixture',$data)
    if($LASTEXITCODE -ne 0){throw '生成夹具失败'}
}
if(!(Test-Path -LiteralPath (Join-Path $data '045-fixture'))){throw '缺少隔离夹具标记'}
$names=@('PANE_DATA_DIR','PANE_INSTANCE','PANE_OBSERVE','PANE_VERIFY','PANE_RESULT','PANE_DEMO','PANE_DEV_HOTKEYS','PANE_CLIPBOARD_PROBE','PANE_CLIPBOARD_PROBE_BASELINE')
$saved=@{};$process=$null
try{
    foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name);[Environment]::SetEnvironmentVariable($name,$null)}
    $env:PANE_DATA_DIR=$data;$env:PANE_INSTANCE=$data;$env:PANE_OBSERVE='1'
    $env:PANE_DEV_HOTKEYS='launcher=Ctrl+Alt+Shift+L;clipboard=Ctrl+Alt+Shift+C'
    $env:PANE_CLIPBOARD_PROBE='045';if($Baseline){$env:PANE_CLIPBOARD_PROBE_BASELINE='1'}
    for($run=1;$run -le 2;$run++){
        $process=Start-Process -FilePath (Join-Path $app 'Pane.App.exe') -ArgumentList @('--autostart') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $data "045-stdout-$run.log") -RedirectStandardError (Join-Path $data "045-stderr-$run.log")
        $null=$process.Handle
        if(!$process.WaitForExit(45000)){throw '隔离探针超时'}
        if($process.ExitCode -ne 0){throw "隔离探针退出失败：$($process.ExitCode)"}
        $result=[IO.File]::ReadAllText((Join-Path $data '045-result.txt'))
        $loaded=($run-1)*3;$total=$run*3
        if($result -notmatch "production=True;loaded=$loaded;saved=$total;storageFailures=0;"){throw "隔离探针结果不符：$result"}
        Write-Output "run=$run;$result"
        $process.Dispose();$process=$null
    }
    Write-Output 'PASS production clipboard: assertions=4; launches=2; realClipboard=0; realRegistryWrites=0'
}finally{
    # 只回收本脚本启动的进程，不查找或终止其他 Pane 实例。
    if($process){if(!$process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()}
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
}
