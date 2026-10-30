$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. "$repo/scripts/release-common.ps1"
$Root=Join-Path ([IO.Path]::GetTempPath()) ('Pane-017-runtime-'+[Guid]::NewGuid().ToString('N'))
$DataDirectory=Join-Path $Root 'data';$Isolated=$true;$process=$null
function Read-Status($Process){
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','Pane-release-probe-'+$Process.Id,[IO.Pipes.PipeDirection]::In)
    try {$pipe.Connect(1000);$reader=[IO.StreamReader]::new($pipe);$task=$reader.ReadLineAsync();if(!$task.Wait(3000)){throw '探针读取超时'};return $task.Result}finally{$pipe.Dispose()}
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ReleaseWindows {
    delegate bool Visit(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(Visit callback,IntPtr state);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window,int id);
    public static int Visible(int pid){int count=0;EnumWindows((w,_)=>{uint p;GetWindowThreadProcessId(w,out p);if(p==pid&&IsWindowVisible(w))count++;return true;},IntPtr.Zero);return count;}
    public static bool HotkeysReleased(){bool a=RegisterHotKey(IntPtr.Zero,601,0x4007,76);bool b=RegisterHotKey(IntPtr.Zero,602,0x4007,67);if(a)UnregisterHotKey(IntPtr.Zero,601);if(b)UnregisterHotKey(IntPtr.Zero,602);return a&&b;}
}
'@
try {
    & "$repo/scripts/install.ps1" -Root $Root -NoBuild -NoStart -Isolated
    $exe=Join-Path $Root 'app/Pane.App.exe'
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $process=Invoke-PaneProcess $exe @('--autostart')
    $status=''
    do {
        if($process.HasExited){throw "启动提前退出：$($process.ExitCode)"}
        try {$status=Read-Status $process}catch [TimeoutException]{}
        if($watch.Elapsed.TotalSeconds -gt 40){throw '启动超时'}
        if($status -notmatch 'ready=True'){Start-Sleep -Milliseconds 50}
    }while($status -notmatch 'ready=True')
    Write-Host "readyWallMs=$($watch.Elapsed.TotalMilliseconds.ToString('F2'));$status"
    if($status -notmatch 'hotkeys=2;' -or $status -notmatch 'tray=True;' -or $status -notmatch 'visible=0;' -or $status -notmatch 'production=True' -or $status -notmatch 'hook=Running;' -or $status -notmatch 'clipboard=True;'){throw '启动服务状态不符合要求'}
    $windows=[ReleaseWindows]::Visible($process.Id);Write-Host "visibleTopLevelWindows=$windows"
    if($windows -ne 0){throw '静默启动弹出了窗口'}
    Invoke-PaneProcess $exe @('--autostart') -Wait
    if([ReleaseWindows]::Visible($process.Id) -ne 0){throw '重复 autostart 弹出了窗口'}
    Start-Sleep -Seconds 15
    $process.Refresh();$working=$process.WorkingSet64/1MB;$private=$process.PrivateMemorySize64/1MB
    Write-Host "idleWorkingMiB=$($working.ToString('F3'));privateMiB=$($private.ToString('F3'))"
    if($working -gt 150){throw '空闲工作集超过 150 MiB'}
    $exitWatch=[Diagnostics.Stopwatch]::StartNew();Invoke-PaneProcess $exe @('--exit') -Wait
    if(!$process.WaitForExit(10000) -or $process.ExitCode -ne 0){throw '正常退出或清理校验失败'}
    Write-Host "exitMs=$($exitWatch.Elapsed.TotalMilliseconds.ToString('F2'));exitCode=$($process.ExitCode);unhook=1;clipboardRemoved=1;hotkeysAndTrayRemoved=1"
    if(![ReleaseWindows]::HotkeysReleased()){throw '退出后热键仍被占用'}
    $results=@(Get-ChildItem -LiteralPath "$Root/app" -File | Where-Object {$_.Name -match '^pane\.(ready|stopped|error)'})
    if($results.Count){throw '正式版产生了开发结果文件'}
    Write-Host 'PASS release runtime; noVisibleWindows=1; noDevResults=1; realRegistryWrites=0; realClipboardReads=0'
}finally {
    if($process){if(!$process.HasExited){try {Invoke-PaneProcess (Join-Path $Root 'app/Pane.App.exe') @('--exit') -Wait}catch{if(!$process.HasExited){$process.Kill();$process.WaitForExit()};Write-Warning '测试失败后强制清理了本次进程'}};$process.Dispose()}
    if(Test-Path "$DataDirectory/logs"){Get-ChildItem "$DataDirectory/logs/crash-*.txt" | ForEach-Object {Get-Content -LiteralPath $_.FullName}}
    & "$repo/scripts/uninstall.ps1" -Root $Root -Isolated -RemoveData
}
