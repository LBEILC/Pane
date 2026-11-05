param([ValidateSet('jit','aot')][string]$Mode='jit',[switch]$HiddenChild,[switch]$Cycles)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/055'
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
if(!$HiddenChild){
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Mode',$Mode,'-HiddenChild')
    if($Cycles){$arguments+='-Cycles'}
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,$arguments,$repo,900000)
    if(Test-Path "$out/modules-$Mode.log"){Get-Content -Encoding UTF8 "$out/modules-$Mode.log"}
    if($code -ne 0){throw "能力测试失败：$code"};return
}
[HiddenDesktop027]::Require()
. "$repo/src/Pane.App/scripts/common.ps1"
$env:PANE_BIN_DIR="$out/build"
[IO.File]::WriteAllText("$env:PANE_BIN_DIR/Pane.App.csproj",'<Project />')
$env:PANE_VERIFY='1';$env:PANE_OBSERVE='1';$env:PANE_TEST_GESTURES='1';$env:PANE_DEV_HOTKEYS=''
$ids=@('launcher','clipboard','gestures','super-panel','ai','folder-jump','rename')
$script:checks=0;$script:lines=@();$samples=@();$process=$null
function Check([bool]$ok,[string]$message){$script:checks++;if(!$ok){throw $message}}
function Verify([string]$status,[string[]]$enabled){
    foreach($id in $ids){$expected=if($enabled -contains $id){'Running'}else{'Disabled'};Check ($status -match "module\.$id=$expected;") "$id != $expected : $status"}
    $mouse=[int](($enabled -contains 'gestures') -or ($enabled -contains 'super-panel'))
    Check ($status -match "mousehook=$mouse;") "mouse hook: $status"
    Check ($status -match 'keyhook=0;') "keyboard hook: $status"
    $folder=if($enabled -contains 'folder-jump'){3}else{0}
    Check ($status -match "folderhook=$folder;") "folder hook: $status"
    Check ($status -match ('recognition='+($enabled -contains 'gestures')+';')) "recognition: $status"
    Check ($status -match ('clipboard='+($enabled -contains 'clipboard')+';')) "clipboard: $status"
    Check ($status -match ('overlay='+($enabled -contains 'gestures')+';')) "overlay: $status"
    $keys=[int]($enabled -contains 'launcher')+[int]($enabled -contains 'clipboard')
    Check ($status -match "hotkeys=$keys;") "hotkeys: $status"
    $panels=[regex]::Match($status,';panels=([^;]*);').Groups[1].Value.Split(',',[StringSplitOptions]::RemoveEmptyEntries)
    $expectedPanels=@();if($enabled -contains 'launcher'){$expectedPanels+='launcher'};if($enabled -contains 'clipboard'){$expectedPanels+='clipboard'};if($enabled -contains 'super-panel'){$expectedPanels+='super'}
    Check (($panels -join ',') -eq ($expectedPanels -join ',')) "panels: $status"
    if($enabled.Count -eq 0){Check ($status -match ';secondary=;') "secondary remains: $status"}
    if($enabled -notcontains 'launcher'){Check ($status -match ';index=none') "index remains: $status"}
}
function Settings([string[]]$enabled){
    $modules=@{};foreach($id in $ids){$modules[$id]=$enabled -contains $id}
    $glass=@{TintOpacity=.2;LuminosityOpacity=.2;Saturation=1;Highlight=.5;Shadow=.5;ExtraBlur=0}
    return @{Version=1;Modules=$modules;General=@{StartWithWindows=$false;ShowTrayIcon=$false;Theme='Light';AccentColor='#FF5874C9';PanelRadius=20;MotionSpeed=1;GlassLight=$glass;GlassDark=$glass};Launcher=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=134}};Clipboard=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=133}};Update=@{AutoUpdate=$false}}
}
try{
    $variants=@(@{Name='all';Ids=$ids},@{Name='none';Ids=@()})
    foreach($id in $ids){$variants+=@{Name=$id;Ids=@($id)}}
    $variants+=@{Name='clipboard-gestures';Ids=@('clipboard','gestures')}
    foreach($variant in $variants){
        $prefix="$out/matrix-$Mode-$($variant.Name)-"+[Guid]::NewGuid().ToString('N');$data=$prefix+'-data'
        New-Item -ItemType Directory -Force $data|Out-Null
        [IO.File]::WriteAllText("$data/settings.json",((Settings $variant.Ids)|ConvertTo-Json -Depth 8))
        $process=Start-Pane $Mode $prefix $false $data
        try{
            Wait-Ready $process $prefix
            Send-Control $process $prefix 'module-status' 'module-status-result'
            $status=[IO.File]::ReadAllText("$prefix.module-status-result");Verify $status $variant.Ids
            $script:lines+="$($variant.Name): $($status.Replace("`n",' | '))"
            if($variant.Name -eq 'none'){
                foreach($command in @('show-launcher','hotkey-launcher','show-clipboard','hotkey-clipboard','show-super','pause-gestures')){
                    Send-Control $process $prefix 'module-command' 'module-command-result' $command
                    Check ([IO.File]::ReadAllText("$prefix.module-command-result") -like 'disabled:*') "disabled command $command"
                }
                if($Cycles){
                    foreach($id in $ids){foreach($round in 1..10){
                        Send-Control $process $prefix 'module-set' 'module-set-result' "$id=1"
                        Verify ([IO.File]::ReadAllText("$prefix.module-set-result")) @($id)
                        if($id -in @('launcher','ai','rename')){
                            Send-Control $process $prefix 'module-open' 'module-open-result' $id
                            $opened=[IO.File]::ReadAllText("$prefix.module-open-result")
                            $expectedWindow=switch($id){'launcher'{'Files'};'ai'{'Translate,Chat'};'rename'{'Rename'}}
                            $actualNames=([regex]::Match($opened,';secondary=([^;]*);').Groups[1].Value.Split(',')|Sort-Object) -join ','
                            $expectedNames=($expectedWindow.Split(',')|Sort-Object) -join ','
                            Check ($actualNames -eq $expectedNames) "secondary ownership $id"
                        }
                        Send-Control $process $prefix 'module-set' 'module-set-result' "$id=0"
                        Verify ([IO.File]::ReadAllText("$prefix.module-set-result")) @()
                        Start-Sleep -Milliseconds 2400
                        Send-Control $process $prefix 'module-status' 'module-status-result'
                        $process.Refresh();$samples+=[pscustomobject]@{Module=$id;Round=$round;WorkingMiB=$process.WorkingSet64/1MB;Status=[IO.File]::ReadAllText("$prefix.module-status-result").Replace("`n",' | ')}
                        $samples|Export-Csv -NoTypeInformation -Encoding UTF8 "$out/cycles-$Mode.csv"
                    }}
                }
            }
        }finally{try{Stop-Pane $process $prefix}finally{$process.Dispose();$process=$null}}
    }
    $script:lines+="PASS assertions=$script:checks; hiddenDesktop=1; userClipboard=0; userRegistry=0"
    if($Cycles){
        $growth=foreach($id in $ids){
            $rows=@($samples|Where-Object Module -eq $id)
            $lastFive=$rows[9].WorkingMiB-$rows[5].WorkingMiB
            $afterThird=$rows[9].WorkingMiB-$rows[2].WorkingMiB
            [pscustomobject]@{Module=$id;LastFiveMiB=$lastFive;ThirdToTenthMiB=$afterThird;Passed=($lastFive -le 3 -and $afterThird -le 10)}
        }
        $growth|Export-Csv -NoTypeInformation -Encoding UTF8 "$out/growth-$Mode.csv"
        foreach($row in $growth){$script:lines+="MEMORY $($row.Module): last5=$($row.LastFiveMiB); round3to10=$($row.ThirdToTenthMiB); passed=$($row.Passed)"}
        if(@($growth|Where-Object Passed -eq $false).Count){throw '功能断言通过，但工作集增长未达到设计阈值，详见 growth CSV。'}
    }
}catch{$script:lines+=$_.ToString();throw}
finally{
    [IO.File]::WriteAllLines("$out/modules-$Mode.log",$script:lines,[Text.UTF8Encoding]::new($false))
    if($process){Stop-Pane $process $prefix}
}
