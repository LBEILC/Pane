param([ValidateSet('before','after')][string]$Label='after',[switch]$Worker)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
$out=Join-Path $repo '.codex-runs/119'
Add-Type -Path "$repo/experiments/027-idle/HiddenDesktop.cs"
if(!$Worker){
    # 开发宿主按上级目录的项目标记启用隔离实例与控制文件。
    [IO.File]::WriteAllText("$out/jit/Pane.App.csproj","<Project />")
    $arguments=@('-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Worker','-Label',$Label)
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,$arguments,$repo,180000)
    Get-Content "$out/$Label.log" -Encoding UTF8
    if($code){throw "隐藏桌面夹具退出码 $code"}
    return
}
[HiddenDesktop027]::Require()
Start-Transcript -LiteralPath "$out/$Label.log" | Out-Null
try {
    Add-Type -AssemblyName System.Windows.Forms,System.Drawing
    Add-Type -Path "$PSScriptRoot/Window119.cs" -ReferencedAssemblies System.Windows.Forms
    . "$repo/src/Pane.App/scripts/common.ps1"
    $env:PANE_BIN_DIR=$out
    $env:PANE_OBSERVE='1'
    $env:PANE_VERIFY=$null
    foreach($route in 'hotkey','double'){
        $prefix=Join-Path $out ([guid]::NewGuid().ToString('N'))
        $env:PANE_DEV_HOTKEYS=if($route -eq 'hotkey'){'launcher=Ctrl+Alt+Shift+J;clipboard=Ctrl+Alt+Shift+K'}else{'launcher=2xRCtrl;clipboard=2xRShift'}
        $p=Start-Pane 'jit' $prefix
        try {
            Wait-Ready $p $prefix
            $ready=Get-Content "$prefix.ready"
            if($route -eq 'hotkey'){
                foreach($target in 'launcher','clipboard'){
                    if(!($ready -match "^$target=.*registered=True")){throw "组合键未注册：$target"}
                }
            } elseif(!($ready -match '^keyhook=1$')){throw '双击识别钩子未启动'}
            $shell=[Window119]::FindWindow('Pane011Shell','Pane')
            if($shell -eq [IntPtr]::Zero){throw '找不到热键宿主'}
            foreach($mode in 'normal','maximized','fullscreen','topmost'){
                $form=New-Object Windows.Forms.Form
                try {
                    switch($mode){
                        'maximized' {$form.WindowState='Maximized'}
                        'fullscreen' {$form.FormBorderStyle='None';$form.StartPosition='Manual';$form.Bounds=[Windows.Forms.Screen]::PrimaryScreen.Bounds}
                        'topmost' {$form.TopMost=$true}
                    }
                    $form.Show()
                    foreach($id in 1,2){
                        $ok=[Window119]::SetForegroundWindow($form.Handle)
                        [Window119]::Pump(100)
                        $fg=[Window119]::GetForegroundWindow()
                        $trace="$prefix.trace"
                        $lines=@(Get-Content $trace)
                        # 投递系统消息或识别完成通知，不向输入桌面注入按键。
                        if($route -eq 'hotkey'){$msg=0x312;$w=$id;$l=0}
                        else{$msg=0x8005;$w=[int](($lines | Where-Object {$_ -like 'generation *'} | Select-Object -Last 1) -split ' ')[1];$l=$id}
                        if(![Window119]::PostMessage($shell,$msg,[IntPtr]$w,[IntPtr]$l)){throw '投递呼出失败'}
                        $limit=[DateTime]::UtcNow.AddSeconds(10)
                        do {
                            [Window119]::Pump(25)
                            $record=@(Get-Content $trace | Select-Object -Skip $lines.Count)
                            $show=$record | Where-Object {$_ -like 'show *'} | Select-Object -Last 1
                        } until($show -or [DateTime]::UtcNow -gt $limit)
                        if(!$show){throw "缺少显示记录 $route $mode $id"}
                        [Window119]::Pump(650)
                        $fields=$show -split ' '
                        $sample=[Window119]::Sample($form.Handle,[IntPtr][long]$fields[1],[IntPtr][long]$fields[2])
                        $events=(Get-Content $trace | Select-Object -Skip $lines.Count) -join ','
                        Write-Output "$route $mode $id app=$($form.Handle) appSet=$ok startFg=$fg $sample events=[$events]"
                        if($sample -notmatch 'top=True/True visible=True/True order=I>V>A$' -or $events -match 'blur '){
                            throw "层序、可见性或失焦收起异常：$sample $events"
                        }
                        if($fg -eq $form.Handle){
                            $expected=if($fields[3] -eq 'True'){$form.Handle.ToInt64()}else{[long]$fields[1]}
                            if($sample -notmatch "^fg=$expected " -or ($fields[3] -eq 'False' -and $sample -notmatch 'panelFocus=True')){
                                throw "前台或键盘焦点异常：$sample"
                            }
                        }
                        Send-Control $p $prefix 'hide' 'hidden'
                    }
                } finally {$form.Dispose()}
            }
        } finally {Stop-Pane $p $prefix}
    }
} catch { Write-Output $_.Exception.ToString();throw } finally {Stop-Transcript | Out-Null}
