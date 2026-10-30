param([ValidateSet('Both','jit','aot')][string]$Mode='Both',[switch]$Cached,[string]$OutputRoot)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$OutputRoot){$OutputRoot=Join-Path $root 'bin'}
$count=& git @('-C',$root,'rev-list','--count','HEAD')
if($LASTEXITCODE -ne 0){throw '无法读取提交数'}
$hash=& git @('-C',$root,'rev-parse','--short','HEAD')
if($LASTEXITCODE -ne 0){throw '无法读取提交哈希'}
$version="0.1.$count"
$modes=if($Mode-eq 'Both'){@('jit','aot')}else{@($Mode)}
foreach($kind in $modes){
    $aot=if($kind-eq 'aot'){'true'}else{'false'}
    $arguments=@('publish',"$root/Pane.App.csproj",'-c','Release','-r','win-x64',"-p:PublishAot=$aot","-p:Version=$version","-p:InformationalVersion=$version+$hash","-p:IncludeSourceRevisionInInformationalVersion=false",'-o',"$OutputRoot/$kind")
    if($Cached){$arguments+=@('--source',"$env:USERPROFILE/.nuget/packages",'-p:NuGetAudit=false')}
    & (Get-Command dotnet.exe -ErrorAction Stop).Source $arguments
    if($LASTEXITCODE-ne 0){throw "$kind 构建失败"}
}
