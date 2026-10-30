param([ValidateSet('Both','jit','aot')][string]$Mode='Both',[switch]$SkipDemo)
. "$PSScriptRoot/common.ps1"
$oldVerify=$env:PANE_VERIFY
try {
$env:PANE_VERIFY='1'
$modes=if($Mode-eq 'Both'){@('jit','aot')}else{@($Mode)}
if(!('PaneHotkeyFixture' -as [type])){Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public sealed class PaneHotkeyFixture : IDisposable {
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h,int id,uint mods,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h,int id);
    readonly ManualResetEventSlim ready=new ManualResetEventSlim();
    readonly ManualResetEventSlim stop=new ManualResetEventSlim();
    readonly Thread thread;
    public bool Reserved;
    public PaneHotkeyFixture(){thread=new Thread(()=>{Reserved=RegisterHotKey(IntPtr.Zero,11911,0x4001,32);ready.Set();stop.Wait(15000);if(Reserved)UnregisterHotKey(IntPtr.Zero,11911);});thread.IsBackground=true;thread.Start();ready.Wait();}
    public void Dispose(){stop.Set();thread.Join();ready.Dispose();stop.Dispose();}
}
"@
}
foreach($kind in $modes){
    $prefix=New-Result "verify-$kind";$process=$null
    try {
        $process=Start-Pane $kind $prefix;Wait-Ready $process $prefix
        $second=Start-Pane $kind $prefix
        if(!$second.WaitForExit(8000)-or $second.ExitCode-ne 0){throw '第二实例未正常退出'}
        Wait-Ready $process $prefix 'forwarded'
        Send-Control $process $prefix 'hide' 'hidden'
        Send-Control $process $prefix 'save-settings' 'settings-saved'
        $original=[IO.File]::ReadAllText("$prefix-data/settings.json")
        if($original-notmatch '"Version": 1'){throw '设置版本未写入'}
        Write-Host ([IO.File]::ReadAllText("$prefix.ready"))
    } finally {Stop-Pane $process $prefix}
    # 重启读回、再验证损坏文件备份恢复；目录仅含本次自测的假设置。
    Remove-Item -LiteralPath "$prefix.ready","$prefix.stopped","$prefix.settings-saved","$prefix.forwarded" -ErrorAction SilentlyContinue
    try {$process=Start-Pane $kind $prefix;Wait-Ready $process $prefix;Send-Control $process $prefix 'save-settings' 'settings-saved';if([IO.File]::ReadAllText("$prefix-data/settings.json")-ne $original){throw '设置往返不一致'}} finally {Stop-Pane $process $prefix}
    [IO.File]::WriteAllText("$prefix-data/settings.json",'{broken')
    Remove-Item -LiteralPath "$prefix.ready","$prefix.stopped" -ErrorAction SilentlyContinue
    try {$process=Start-Pane $kind $prefix;Wait-Ready $process $prefix;Wait-Ready $process $prefix 'settings-recovered';if(!(Get-ChildItem -LiteralPath "$prefix-data" -Filter 'settings.json.bad-*')){throw '缺少坏文件备份'}} finally {Stop-Pane $process $prefix}
    $fixture=New-Object PaneHotkeyFixture
    $conflictPrefix=New-Result "conflict-$kind";$process=$null
    try {
        $process=Start-Pane $kind $conflictPrefix;Wait-Ready $process $conflictPrefix;Wait-Ready $process $conflictPrefix 'hotkey-conflicts'
        if([IO.File]::ReadAllText("$conflictPrefix.hotkey-conflicts")-notmatch 'launcher;1409'){throw '缺少 Alt+空格 被占用事件'}
        Write-Host "Alt+空格冲突事件已验证；自测夹具占用=$($fixture.Reserved)；记录=$conflictPrefix"
    } finally {try {Stop-Pane $process $conflictPrefix} finally {$fixture.Dispose()}}
    if(!$SkipDemo){& "$PSScriptRoot/capture.ps1" -Mode $kind -Theme Light -Shot all -NoShot}
    Write-Host "验证完成：$prefix"
}

} finally {$env:PANE_VERIFY=$oldVerify}
