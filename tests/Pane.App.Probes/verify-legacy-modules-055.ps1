param([ValidateSet('jit','aot')][string]$Mode='jit',[switch]$HiddenChild)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $repo '.codex-runs/055-patch'
Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
if(!$HiddenChild){
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Mode',$Mode,'-HiddenChild'),$repo,120000)
    Get-Content "$out/legacy-$Mode.log" -Encoding UTF8
    if($code -ne 0){throw "旧开关迁移验证失败：$code"};return
}
[HiddenDesktop027]::Require()
. "$repo/src/Pane.App/scripts/common.ps1"
$env:PANE_BIN_DIR="$out/build"
[IO.File]::WriteAllText("$env:PANE_BIN_DIR/Pane.App.csproj",'<Project />')
$env:PANE_VERIFY='1';$env:PANE_OBSERVE='1';$env:PANE_TEST_GESTURES='1';$env:PANE_DEV_HOTKEYS=''
$prefix="$out/legacy-$Mode-"+[Guid]::NewGuid().ToString('N');$data=$prefix+'-data'
New-Item -ItemType Directory -Force $data|Out-Null
$glass=@{TintOpacity=.2;LuminosityOpacity=.2;Saturation=1;Highlight=.5;Shadow=.5;ExtraBlur=0}
$settings=@{Version=1;General=@{StartWithWindows=$false;ShowTrayIcon=$false;Theme='Light';AccentColor='#FF5874C9';PanelRadius=20;MotionSpeed=1;GlassLight=$glass;GlassDark=$glass};Launcher=@{Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=134}};Clipboard=@{Enabled=$false;Hotkey=@{Modifiers='Ctrl, Alt, Shift';VirtualKey=133}};SuperPanel=@{Enabled=$false};FolderJump=@{Enabled=$false};Ai=@{Disabled=$true;ApiBaseUrl='https://example.test/v1';TranslateTarget='auto';Actions=@()};Update=@{AutoUpdate=$false}}
[IO.File]::WriteAllText("$data/settings.json",($settings|ConvertTo-Json -Depth 8))
$process=$null;$lines=@();$checks=0
function Check([bool]$ok,[string]$message){$script:checks++;if(!$ok){throw $message}}
try{
    $process=Start-Pane $Mode $prefix $false $data
    Wait-Ready $process $prefix
    Send-Control $process $prefix 'module-status' 'module-status-result'
    $before=[IO.File]::ReadAllText("$prefix.module-status-result");$lines+=$before
    Check ($before -match 'module.ai=Disabled;enabled=False;') '旧 AI 关闭状态没有迁移'
    $saved=Get-Content "$data/settings.json" -Raw -Encoding UTF8|ConvertFrom-Json
    Check ($saved.Ai.Disabled -eq $true -and $saved.Modules.ai -eq $false) '迁移后的 AI 字段不一致'
    Send-Control $process $prefix 'module-set' 'module-set-result' 'ai=1'
    $after=[IO.File]::ReadAllText("$prefix.module-set-result");$lines+=$after
    Check ($after -match 'module.ai=Running;enabled=True;') 'AI 未运行'
    $saved=Get-Content "$data/settings.json" -Raw -Encoding UTF8|ConvertFrom-Json
    # Ai.Enabled 的定义为 !Disabled；启动器用它与模块运行状态共同控制入口。
    Check ($saved.Ai.Disabled -eq $false -and $saved.Modules.ai -eq $true) '启动器 AI 门控仍关闭'
}catch{$lines+=$_.ToString();throw}
finally{
    try{if($process){Stop-Pane $process $prefix;$checks++;$lines+='cleanExit=True'}}finally{
        if($process){$process.Dispose()}
        $lines+="assertions=$checks; hiddenDesktop=1; isolatedData=1"
        [IO.File]::WriteAllLines("$out/legacy-$Mode.log",$lines,[Text.UTF8Encoding]::new($false))
    }
}
