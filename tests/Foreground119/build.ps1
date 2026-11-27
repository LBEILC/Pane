param([switch]$Baseline)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../..").Path
$out=Join-Path $repo '.codex-runs/119'
New-Item -ItemType Directory -Force $out | Out-Null
# 在临时编译副本插入记录点，正式构建不包含探针。
function Instrument($file,$pairs){
    $source=[IO.File]::ReadAllText("$repo/src/Pane.App/Hosting/$file")
    if($Baseline -and $file -eq 'GlassPanel.cs'){
        $source=(& git @('-C',$repo,'show','bd2d80a:src/Pane.App/Hosting/GlassPanel.cs')) -join [Environment]::NewLine
        if($LASTEXITCODE){throw '无法读取 119 修前版本'}
    }
    foreach($pair in $pairs){
        if(!$source.Contains($pair[0])){throw "记录点已变化：$($pair[0])"}
        $source=$source.Replace($pair[0],$pair[1])
    }
    [IO.File]::WriteAllText("$out/$file",$source)
}
# 旧版在条件末尾直接收起；当前版在钉住分支后收起。只在实际收起时记录。
$blur=if($Baseline){
    @('&&(HideOnBlur?.Invoke()??true))Hide();','&&(HideOnBlur?.Invoke()??true)){Probe119.Log("blur " + l);Hide();}')
}else{
    @('            else Hide();','            else {Probe119.Log("blur " + l);Hide();}')
}
Instrument 'GlassPanel.cs' @(
    ,@('Native.SetForegroundWindow(Input)','Probe119.Foreground(Input)')
    ,@('outsideButtons=MouseButtonsDown();','Probe119.Shown(Input,Visual,Passive,Shown);outsideButtons=MouseButtonsDown();')
    ,$blur
)
Instrument 'ShellHost.cs' @(,@('keyboardGeneration++;StopKeyboardHook();','keyboardGeneration++;Probe119.Log("generation " + keyboardGeneration);StopKeyboardHook();'))
. "$repo/scripts/release-version.ps1"
$version=Get-PaneReleaseVersion "$repo/src/Pane.App/ReleaseNotes"
$args119=@('publish',"$repo/src/Pane.App/Pane.App.csproj",'-c','Release','-r','win-x64','-p:PublishAot=false',"-p:Version=$version",'-p:IntermediateOutputPath=obj/Foreground119/','-p:OutputPath=bin/Foreground119/',"-p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot/probe.targets","-p:ProbeDirectory=$out",'--source',"$env:USERPROFILE/.nuget/packages",'-p:NuGetAudit=false','-o',"$out/jit")
& dotnet $args119
if($LASTEXITCODE){throw '探针构建失败'}
