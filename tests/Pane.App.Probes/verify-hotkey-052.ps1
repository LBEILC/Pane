param([ValidateSet('jit','aot')][string]$Mode='jit',[switch]$HiddenChild)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$HiddenChild){
    Add-Type -Path (Join-Path $repo 'experiments/027-idle/HiddenDesktop.cs')
    $code=[HiddenDesktop027]::Run((Get-Command powershell.exe).Source,@('-NoProfile','-ExecutionPolicy','RemoteSigned','-File',$PSCommandPath,'-Mode',$Mode,'-HiddenChild'),$repo,55000)
    if($code -ne 0){throw "隐藏桌面热键测试失败：$code"}
    Get-Content -LiteralPath "$repo/.codex-runs/052/hotkey-$Mode.log"
    return
}
. "$repo/src/Pane.App/scripts/common.ps1"
$prefix=New-Result '052-hotkey';$process=$null
try {
    $process=Start-Pane $Mode $prefix $true
    Wait-Ready $process $prefix
    Send-Control $process $prefix 'hotkey-probe' 'hotkey-result'
    [IO.File]::WriteAllText("$repo/.codex-runs/052/hotkey-$Mode.log",[IO.File]::ReadAllText("$prefix.hotkey-result"))
} finally {Stop-Pane $process $prefix}
